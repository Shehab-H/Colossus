import { Type, type Table, type Vector } from 'apache-arrow';
import type { CompanionPack, Manifest, ViewConfig } from './manifest';
import { factsUrl, packBlockUrl, tileUrl } from './manifest';
import { fetchArrowBlock, fetchArrowTable } from './arrow';
import { isGroupRegime, measureChannels, NUMERIC_TYPES } from './channels';
import type { CompanionData, CompanionDim } from './measures';
import { buildFilterValues, canonicalCodeLut, dayNumber, MISSING_CODE, type FilterSlots } from './gpuFilter';
import { TileColumns } from './schema';

/** A per-mark channel column in transfer-friendly form. Row-wise JS strings never cross the worker
 *  boundary (cloning ~1M of them per tile was the zoom stutter): categorical columns become integer
 *  codes + a small dictionary, high-cardinality identity columns stay raw UTF-8 decoded per row on
 *  demand (a click), numeric columns stay typed arrays. The string[] form is a last-resort fallback
 *  (multi-chunk or null-bearing high-cardinality columns) that still structured-clones. */
export type DictColumn = { kind: 'dict'; codes: Uint8Array | Uint16Array | Uint32Array; dict: string[] };
export type Utf8Column = { kind: 'utf8'; bytes: Uint8Array; offsets: Int32Array };
export type TileColumn = Float32Array | DictColumn | Utf8Column | string[];

/** A loaded tile, read straight from its Arrow buffers. Geometry is built once (stable across camera
 *  moves, measure switches, and filter changes); the per-mark channel values ride along so recoloring —
 *  and answering a click — is a client-side scan, not a re-fetch. Filtering is GPU-side: `filterValues`
 *  is a per-slot binary attribute the DataFilterExtension tests against uniforms (see lib/gpuFilter).
 *  Colors are derived at layer time (see lib/deckData). */
export interface TileData {
  count: number; // marks in the tile (polygons or points) — resident, pre-filter
  // Polygon marks: deck SolidPolygonLayer binary layout, mostly zero-copy from Arrow's list buffers.
  polyPositions?: Float32Array; // flat [x0,y0,x1,y1,…], every ring concatenated
  polyStartIndices?: Uint32Array; // vertex offset per polygon; length = count + 1
  vertexCount?: number; // total vertices across all polygons
  polyTriangles?: Uint32Array; // bake-time triangle indices (tile-global) — deck skips earcut entirely
  // Point marks:
  positions?: Float32Array; // interleaved [x0,y0,…]
  // GPU filter slots: interleaved [slot0,slot1,…] per mark (points) or per vertex (polygons). Absent
  // when the view has no filterable channels.
  filterValues?: Float32Array;
  // One column per measure plus any channel named by `inspect`, keyed by channel name.
  values: Record<string, TileColumn>;
  // Format 2 only: the single fetched ArrayBuffer every heavy column above is a view into. Held as the
  // retention anchor (and transferred once) so the tile's bytes flow network → worker → GPU with no copy.
  buffer?: ArrayBuffer;
}

const utf8Decoder = new TextDecoder();

/** Read one mark's value out of any column form (the only row-wise string decode left, on click). */
export function columnValue(col: TileColumn | undefined, i: number): number | string | undefined {
  if (!col) return undefined;
  if (col instanceof Float32Array) return col[i];
  if (Array.isArray(col)) return col[i];
  if (col.kind === 'dict') return col.dict[col.codes[i]];
  return utf8Decoder.decode(col.bytes.subarray(col.offsets[i], col.offsets[i + 1]));
}

/** Approximate resident bytes of a tile — what the cache budgets against (see TileCache). For a
 *  format-2 tile the retained `buffer` is counted once; columns that are views into it (their
 *  `.buffer === d.buffer`) are already inside that total, so only separately-built arrays add to it. */
export function tileBytes(d: TileData): number {
  const anchor = d.buffer;
  let b = anchor?.byteLength ?? 0;
  // Count an array's bytes unless it's a view already inside the retained buffer (format 2).
  const own = (v?: ArrayBufferView) => (!v || (anchor && v.buffer === anchor) ? 0 : v.byteLength);
  b += own(d.positions) + own(d.polyPositions) + own(d.polyStartIndices) + own(d.polyTriangles) + own(d.filterValues);
  for (const col of Object.values(d.values)) {
    if (col instanceof Float32Array) b += own(col);
    else if (Array.isArray(col)) b += col.length * 24; // rough JS string+slot overhead
    else if (col.kind === 'dict') b += own(col.codes) + col.dict.length * 16;
    else b += own(col.bytes) + own(col.offsets);
  }
  return b;
}

export async function loadTile(
  view: ViewConfig,
  version: string,
  key: string,
  slots: FilterSlots | null,
  tileFormat: number,
  signal?: AbortSignal,
): Promise<TileData> {
  const [z, x, y] = key.split('/').map(Number);
  const { table, buffer } = await fetchArrowTable(tileUrl(view.id, version, z, x, y), signal);
  return decodeTile(view, table, slots, tileFormat, buffer);
}

/** Which companion columns are grain (and their temporality) — the minimal, structured-clone-friendly
 *  slice of the manifest the worker needs to decode a companion. */
export interface CompanionGrain {
  name: string;
  temporal: boolean;
}

export const companionGrain = (manifest: Manifest): CompanionGrain[] =>
  (manifest.grainChannels ?? []).map((g) => {
    const ch = manifest.view.source.channels.find((c) => c.name === g);
    return { name: g, temporal: ch?.role === 'temporal' || ch?.type === 'date' };
  });

/** One leaf tile's block in the companion pack — where to range-read it and how to decompress it.
 *  Resolved on the main thread (the manifest lives there) and shipped to the worker per request. */
export interface PackBlock {
  url: string;
  offset: number;
  length: number;
  codec: CompressionFormat;
}

/** The pack block for a tile, or null when this tile isn't packed (internal level, older bake, or a
 *  leaf that baked no companion) — null routes to the per-file companion fetch. */
export function packBlock(
  pack: CompanionPack | undefined,
  viewId: string,
  version: string,
  key: string,
): PackBlock | null {
  const entry = pack?.entries[key];
  if (!pack || !entry) return null;
  return { url: packBlockUrl(viewId, version, pack.file, key), offset: entry[0], length: entry[1], codec: pack.codec };
}

/** Decode a fact companion (.facts.arrow) into the typed shape the fold reads: `mki` (each row's mark
 *  index in the render tile, written by the bake), grain dimensions as dict codes (canonical order),
 *  grain temporal values as day numbers, and every partial column as f32. No per-row strings — a
 *  low-zoom companion runs to millions of rows, and every array here transfers across the worker
 *  boundary. Throws on a companion without `mki` (an old bake) — the caller skips the fold. */
export function decodeCompanion(table: Table, grain: CompanionGrain[]): CompanionData {
  const n = table.numRows;

  const mkiCol = table.getChild('mki');
  if (!mkiCol) throw new Error('companion has no mki column (re-bake the view)');
  const mki = int32Column(mkiCol, n);

  const dim: Record<string, CompanionDim> = {};
  const temporalDays: Record<string, Float32Array> = {};
  for (const g of grain) {
    const col = table.getChild(g.name);
    if (!col) continue;
    if (g.temporal) temporalDays[g.name] = temporalDaysColumn(col, n);
    else dim[g.name] = dictFromArrow(col, plainString) ?? dictByScan(col, n);
  }

  const skip = new Set<string>([...grain.map((g) => g.name), 'mki']);
  const partial: Record<string, Float32Array> = {};
  for (const field of table.schema.fields) {
    if (skip.has(field.name)) continue;
    const col = table.getChild(field.name);
    if (col) partial[field.name] = numericColumn(col);
  }
  return { rowCount: n, mki, dim, temporalDays, partial };
}

/** Fetch + decode a tile's fact companion: a packed leaf ranges its block out of the archive (R2);
 *  everything else — internal levels, older bakes — fetches the per-tile file. */
export async function loadCompanion(
  viewId: string,
  version: string,
  key: string,
  grain: CompanionGrain[],
  pack?: PackBlock | null,
  signal?: AbortSignal,
): Promise<CompanionData> {
  const [z, x, y] = key.split('/').map(Number);
  const { table } = pack
    ? await fetchArrowBlock(pack.url, pack.offset, pack.length, pack.codec, signal)
    : await fetchArrowTable(factsUrl(viewId, version, z, x, y), signal);
  return decodeCompanion(table, grain);
}

/** Single-chunk non-null Int32 column viewed in place, else copied. */
function int32Column(col: Vector, n: number): Int32Array {
  const d = col.data.length === 1 && col.nullCount === 0 ? col.data[0] : undefined;
  if (d && d.values instanceof Int32Array) return d.values.subarray(d.offset, d.offset + n);
  return Int32Array.from(col.toArray() as ArrayLike<number>);
}

/** A temporal companion column as day numbers. Arrow DateDay stores days directly (viewed, then widened
 *  to f32); a dictionary-encoded date converts its small dictionary once; anything else falls back to a
 *  per-row conversion. */
function temporalDaysColumn(col: Vector, n: number): Float32Array {
  const ad = arrowDict(col);
  if (ad) {
    const lut = new Float32Array(ad.dict.length);
    for (let c = 0; c < ad.dict.length; c++) lut[c] = dayNumber(ad.dict.get(c));
    const out = new Float32Array(n);
    for (let i = 0; i < n; i++) out[i] = lut[ad.codes[i]];
    return out;
  }
  const d = col.data.length === 1 && col.nullCount === 0 ? col.data[0] : undefined;
  if (col.type.typeId === Type.Date && d && d.values instanceof Int32Array) {
    // DateDay: the underlying int32 IS the day number.
    return Float32Array.from(d.values.subarray(d.offset, d.offset + n));
  }
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) out[i] = dayNumber(col.get(i));
  return out;
}

/** Dict codes by scanning a small non-dictionary column (companion grain cardinality is tiny). */
function dictByScan(col: Vector, n: number): CompanionDim {
  const codeOf = new Map<string, number>();
  const dict: string[] = [];
  const codes = new Uint32Array(n);
  for (let i = 0; i < n; i++) {
    const s = String(col.get(i));
    let c = codeOf.get(s);
    if (c === undefined) {
      c = dict.length;
      codeOf.set(s, c);
      dict.push(s);
    }
    codes[i] = c;
  }
  return { codes: packCodes(codes, dict.length), dict };
}

/** Arrow table → TileData. Pure (no fetch) so the decode is unit-testable. The whole tile is decoded
 *  unconditionally — filtering is a GPU uniform now, never a reason to drop rows here. Filter slot
 *  values are built once, per tile, alongside geometry. Format 2 decodes as views over the retained
 *  `buffer` (no column copies); format 1 (older bakes) keeps the copy path below. */
export function decodeTile(
  view: ViewConfig,
  table: Table,
  slots?: FilterSlots | null,
  tileFormat?: number,
  buffer?: ArrayBuffer,
): TileData {
  if ((tileFormat ?? 1) >= 2 && buffer) return decodeTileV2(view, table, slots, buffer);

  const values = readFields(view, table);

  if (view.mark === 'polygon') {
    const poly = readPolygons(table);
    const filterValues = buildSlotValues(table, slots, poly.count, poly.polyStartIndices, poly.vertexCount);
    return { ...poly, values, filterValues };
  }

  const xs = table.getChild(TileColumns.x)!.toArray() as ArrayLike<number>;
  const ys = table.getChild(TileColumns.y)!.toArray() as ArrayLike<number>;
  const n = xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  const filterValues = buildSlotValues(table, slots, n);
  return { count: n, positions, values, filterValues };
}

/** Format-2 zero-copy decode. The tile is one ArrayBuffer for its whole life; every heavy column is a
 *  typed-array view into it — measures viewed as f32, dict codes reinterpreted in place, geometry and
 *  the tile-global triangle indices viewed with no rebase. Only the small derived arrays are built:
 *  point positions (interleaved x/y), polyStartIndices, per-utf8 offsets, and filterValues. */
function decodeTileV2(view: ViewConfig, table: Table, slots: FilterSlots | null | undefined, buffer: ArrayBuffer): TileData {
  const values = readFieldsView(view, table);

  if (view.mark === 'polygon') {
    const poly = readPolygonsView(table);
    const filterValues = buildSlotValues(table, slots, poly.count, poly.polyStartIndices, poly.vertexCount);
    return { ...poly, values, filterValues, buffer };
  }

  const xs = viewFloat32(table.getChild(TileColumns.x)!, TileColumns.x);
  const ys = viewFloat32(table.getChild(TileColumns.y)!, TileColumns.y);
  const n = table.numRows;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  const filterValues = buildSlotValues(table, slots, n);
  return { count: n, positions, values, filterValues, buffer };
}

/** Per-tile GPU filter attribute: one float slot per filterable channel per mark (points) or vertex
 *  (polygons). Temporal slots hold day numbers; dimension slots hold canonical category codes, remapped
 *  from the tile-local dictionary so the code space is one per (view, version). */
function buildSlotValues(
  table: Table,
  slots: FilterSlots | null | undefined,
  count: number,
  polyStartIndices?: Uint32Array,
  vertexCount?: number,
): Float32Array | undefined {
  if (!slots) return undefined;
  const n = table.numRows;
  const perMark = slots.specs.map((spec) => {
    const col = table.getChild(spec.name);
    if (!col) return new Float32Array(n).fill(MISSING_CODE); // tile lacks the column → kept by (all), matched by nothing
    return spec.kind === 'temporal' ? slotDayNumbers(col, n) : slotCanonicalCodes(col, n, spec.categories);
  });
  return buildFilterValues(slots.size, count, perMark, polyStartIndices, vertexCount);
}

/** Single-chunk Arrow dictionary access (the index buffer IS the code space; the dictionary holds the
 *  logical values) — lets a slot decode the small dictionary once and gather codes, never a per-row scan. */
function arrowDict(col: Vector): { codes: ArrayLike<number>; dict: Vector } | null {
  if (col.type.typeId !== Type.Dictionary || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { values: ArrayLike<number>; dictionary?: Vector };
  return d.dictionary ? { codes: d.values, dict: d.dictionary } : null;
}

/** Per-mark day numbers for a temporal slot (mirrors gpuFilter.dayNumber). */
function slotDayNumbers(col: Vector, n: number): Float32Array {
  const out = new Float32Array(n);
  const ad = arrowDict(col);
  if (ad) {
    const lut = new Float32Array(ad.dict.length);
    for (let c = 0; c < ad.dict.length; c++) lut[c] = dayNumber(ad.dict.get(c));
    for (let i = 0; i < n; i++) out[i] = lut[ad.codes[i]];
    return out;
  }
  for (let i = 0; i < n; i++) out[i] = dayNumber(col.get(i));
  return out;
}

/** Per-mark canonical category codes for a dimension slot (tile-local dict → canonical code LUT). */
function slotCanonicalCodes(col: Vector, n: number, categories: string[] | undefined): Float32Array {
  const out = new Float32Array(n);
  const ad = arrowDict(col);
  if (ad) {
    const dict = new Array<string>(ad.dict.length);
    for (let c = 0; c < ad.dict.length; c++) dict[c] = String(ad.dict.get(c));
    const lut = canonicalCodeLut(dict, categories);
    for (let i = 0; i < n; i++) out[i] = lut[ad.codes[i]];
    return out;
  }
  const codeOf = new Map<string, number>();
  const dict: string[] = [];
  const codes = new Uint32Array(n);
  for (let i = 0; i < n; i++) {
    const s = String(col.get(i));
    let c = codeOf.get(s);
    if (c === undefined) {
      c = dict.length;
      codeOf.set(s, c);
      dict.push(s);
    }
    codes[i] = c;
  }
  const lut = canonicalCodeLut(dict, categories);
  for (let i = 0; i < n; i++) out[i] = lut[codes[i]];
  return out;
}

/** Pull the columns we actually use out of the tile into standalone columns: every measure (kept numeric
 *  for the color ramp) plus any channel named by `inspect`. We COPY rather than hold Arrow's view — a
 *  view keeps the whole tile message alive (all columns + Arrow overhead), so copying only what we need
 *  and letting the Table be GC'd is what keeps the heap bounded. */
function readFields(view: ViewConfig, table: Table): Record<string, TileColumn> {
  const { specByName, names } = fieldSelection(view);
  const values: Record<string, TileColumn> = {};
  for (const name of names) {
    const col = table.getChild(name);
    if (!col) continue;
    const spec = specByName.get(name);
    const temporal = spec?.role === 'temporal' || spec?.type === 'date';
    values[name] = NUMERIC_TYPES.has(spec?.type ?? '')
      ? numericColumn(col)
      : stringColumn(col, spec?.role === 'identity', table.numRows, temporal ? isoDate : plainString);
  }
  return values;
}

/** Which columns land in `values` and their declared specs: every measure, the color channel (may be a
 *  dimension), and any inspect channel/title. Shared by the copy (readFields) and view (readFieldsView)
 *  paths so both select and type columns identically. */
function fieldSelection(view: ViewConfig) {
  const specByName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  const names = new Set<string>();
  for (const ch of measureChannels(view)) names.add(ch.name);
  if (view.encoding?.color?.channel) names.add(view.encoding.color.channel);
  for (const name of view.inspect?.channels ?? []) names.add(name);
  if (view.inspect?.title) names.add(view.inspect.title);
  // Group regime: the mark id aligns each mark to its fact companion during the fold.
  if (isGroupRegime(view)) names.add(TileColumns.id);
  return { specByName, names };
}

/** Format-2 counterpart of readFields: each column is a view into the tile buffer — measures viewed as
 *  f32, dict codes reinterpreted in place, identity UTF-8 viewed. No gather, no per-row string work. */
function readFieldsView(view: ViewConfig, table: Table): Record<string, TileColumn> {
  const { specByName, names } = fieldSelection(view);
  const values: Record<string, TileColumn> = {};
  for (const name of names) {
    const col = table.getChild(name);
    if (!col) continue;
    const spec = specByName.get(name);
    const temporal = spec?.role === 'temporal' || spec?.type === 'date';
    values[name] = NUMERIC_TYPES.has(spec?.type ?? '')
      ? viewFloat32(col, name)
      : stringColumnView(col, spec?.role === 'identity', table.numRows, temporal ? isoDate : plainString);
  }
  return values;
}

/** A single-chunk f32 Arrow column as a view over the tile buffer, bounded to its logical rows (Arrow
 *  pads buffers, so the raw `values` can be longer). Format 2 casts every measure to REAL at bake, so
 *  this must succeed; a non-f32 or chunked/null column is a contract violation, thrown so a bad bake
 *  fails visibly rather than silently degrading to a copy. */
function viewFloat32(col: Vector, name: string): Float32Array {
  const d = col.data.length === 1 && col.nullCount === 0 ? col.data[0] : undefined;
  if (d && d.values instanceof Float32Array) return d.values.subarray(d.offset, d.offset + d.length);
  throw new Error(`format 2: column '${name}' is not a single-chunk non-null Float32 (unviewable)`);
}

/** Format-2 non-numeric column as a view: identity → raw UTF-8 view; else the Arrow dictionary's codes
 *  reinterpreted in place (its dictionary is already canonical, so no remap). Falls back to the copy
 *  path only for shapes views can't express (multi-chunk, null-bearing, or non-dict primitives). */
function stringColumnView(col: Vector, identity: boolean, n: number, norm: (v: unknown) => string): TileColumn {
  if (identity) {
    const u = utf8ColumnView(col, n);
    if (u) return u;
  }
  const d = dictColumnView(col, n, norm);
  if (d) return d;
  return stringColumn(col, identity, n, norm);
}

/** Arrow dictionary column with its index buffer viewed in place (reinterpreted to the matching unsigned
 *  width — codes are non-negative, so the bytes are identical). The small dictionary is decoded once. */
function dictColumnView(col: Vector, n: number, norm: (v: unknown) => string): DictColumn | null {
  if (col.type.typeId !== Type.Dictionary || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { values: ArrayBufferView; dictionary?: Vector; offset: number };
  const dv = d.dictionary;
  if (!dv) return null;
  const dict = new Array<string>(dv.length);
  for (let i = 0; i < dv.length; i++) dict[i] = norm(dv.get(i));
  return { kind: 'dict', codes: reinterpretCodes(d.values, d.offset, n), dict };
}

/** Reinterpret Arrow's signed dictionary indices (Int8/16/32) as the matching unsigned typed array over
 *  the same bytes — a view, not a copy — bounded to the logical `[offset, offset+n)` rows. Safe because
 *  canonical codes are ≥ 0 (no sign bit set). */
function reinterpretCodes(idx: ArrayBufferView, offset: number, n: number): DictColumn['codes'] {
  const buf = idx.buffer as ArrayBuffer;
  if (idx instanceof Int8Array || idx instanceof Uint8Array) return new Uint8Array(buf, idx.byteOffset + offset, n);
  if (idx instanceof Int16Array || idx instanceof Uint16Array) return new Uint16Array(buf, idx.byteOffset + offset * 2, n);
  return new Uint32Array(buf, idx.byteOffset + offset * 4, n);
}

/** Single-chunk UTF-8 column with its bytes viewed in place; only the tiny (n+1) offset array is rebuilt
 *  (rebased to the view's start), so click-to-inspect (columnValue) reads unchanged. */
function utf8ColumnView(col: Vector, n: number): Utf8Column | null {
  if (col.type.typeId !== Type.Utf8 || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { valueOffsets: Int32Array; values: Uint8Array; offset: number };
  const off = d.valueOffsets;
  const base = off[d.offset];
  const bytes = d.values.subarray(base, off[d.offset + n]);
  const offsets = new Int32Array(n + 1);
  for (let i = 0; i <= n; i++) offsets[i] = off[d.offset + i] - base;
  return { kind: 'utf8', bytes, offsets };
}

const plainString = (v: unknown): string => String(v);

/** Temporal values normalize to YYYY-MM-DD no matter how the tile stored them (real dates, day-counts,
 *  or epoch-millis) — the one canonical form that filters, baked manifest domains, and inspect share. */
const isoDate = (v: unknown): string => {
  if (v === null || v === undefined) return 'null';
  if (v instanceof Date) return v.toISOString().slice(0, 10);
  const n = Number(v);
  if (!Number.isFinite(n)) return String(v);
  const ms = Math.abs(n) < 1e7 ? n * 86400000 : n; // day-count vs epoch-millis storage
  return new Date(ms).toISOString().slice(0, 10);
};

function numericColumn(col: Vector): Float32Array {
  const a = col.toArray() as ArrayLike<number>;
  return a instanceof Float32Array ? a.slice() : Float32Array.from(a);
}

// Past this many distinct values a column isn't categorical in any useful sense — stop scanning.
const DICT_CAP = 65536;

/** Non-numeric column → the cheapest faithful form. Identity channels (per-row unique by design, e.g. a
 *  place name) skip straight to raw UTF-8; everything else dict-codes, bailing to raw UTF-8 if the
 *  cardinality cap is hit. All row-wise string work happens here, in the worker, where strings die young. */
function stringColumn(col: Vector, identity: boolean, n: number, norm: (v: unknown) => string): TileColumn {
  if (identity) {
    const u = utf8Column(col, n);
    if (u) return u;
  }
  const direct = dictFromArrow(col, norm);
  if (direct) return direct;

  const codeOf = new Map<string, number>();
  const dict: string[] = [];
  const codes = new Uint32Array(n);
  for (let i = 0; i < n; i++) {
    const s = norm(col.get(i));
    let c = codeOf.get(s);
    if (c === undefined) {
      if (dict.length >= DICT_CAP) return utf8Column(col, n) ?? materializeStrings(col, n, norm);
      c = dict.length;
      codeOf.set(s, c);
      dict.push(s);
    }
    codes[i] = c;
  }
  return { kind: 'dict', codes: packCodes(codes, dict.length), dict };
}

const packCodes = (codes: Uint32Array, cardinality: number): DictColumn['codes'] =>
  cardinality <= 256 ? Uint8Array.from(codes) : cardinality <= 65536 ? Uint16Array.from(codes) : codes;

/** Arrow dictionary-encoded column: the indices ARE the codes — no per-row scan at all; only the small
 *  dictionary itself goes through the normalizer. */
function dictFromArrow(col: Vector, norm: (v: unknown) => string): DictColumn | null {
  if (col.type.typeId !== Type.Dictionary || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { values: ArrayLike<number>; dictionary?: Vector };
  const dv = d.dictionary;
  if (!dv) return null;
  const dict = new Array<string>(dv.length);
  for (let i = 0; i < dv.length; i++) dict[i] = norm(dv.get(i));
  const n = (d.values as ArrayLike<number>).length;
  const codes = new Uint32Array(n);
  for (let i = 0; i < n; i++) codes[i] = d.values[i];
  return { kind: 'dict', codes: packCodes(codes, dict.length), dict };
}

/** Compact copy of a single-chunk UTF-8 column's raw buffers (offsets rebased to 0). Declined for
 *  null-bearing columns — the dict/string paths preserve today's String(null) → "null" rendering. */
function utf8Column(col: Vector, n: number): Utf8Column | null {
  if (col.type.typeId !== Type.Utf8 || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { valueOffsets: Int32Array; values: Uint8Array };
  const off = d.valueOffsets;
  const base = off[0];
  const bytes = d.values.slice(base, off[n]);
  const offsets = new Int32Array(n + 1);
  for (let i = 0; i <= n; i++) offsets[i] = off[i] - base;
  return { kind: 'utf8', bytes, offsets };
}

function materializeStrings(col: Vector, n: number, norm: (v: unknown) => string): string[] {
  const s = new Array<string>(n);
  for (let i = 0; i < n; i++) s[i] = norm(col.get(i));
  return s;
}

/** Extract deck's binary polygon layout from Arrow's `geometry` list column: one contiguous coordinate
 *  buffer + per-polygon vertex offsets. The coordinates are copied out (see readFields) so the Arrow
 *  message — including the `x`/`y`/`part_offsets` columns a polygon mark never uses — can be released. */
function readPolygons(
  table: Table,
): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild(TileColumns.geometry);
  if (!gv) throw new Error('polygon tile has no geometry column');
  const n = table.numRows;

  if (gv.data.length === 1) {
    const d = gv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array }[] };
    const offsets = d.valueOffsets; // element (float) units into the child buffer
    const base = offsets[0]; // float offset of the first ring (0 for our freshly-written tiles)
    const positions = d.children[0].values.slice(base, offsets[n]); // compact copy of just this tile's coords
    const start = new Uint32Array(n + 1);
    for (let i = 0; i <= n; i++) start[i] = (offsets[i] - base) >> 1; // floats → vertices
    const polyTriangles = readTriangles(table, start);
    return { polyPositions: positions, polyStartIndices: start, vertexCount: positions.length >> 1, count: n, polyTriangles };
  }

  // Fallback (multi-chunk): concatenate ring by ring.
  const pos: number[] = [];
  const start = new Uint32Array(n + 1);
  for (let i = 0; i < n; i++) {
    const sub = gv.get(i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    for (let j = 0; j < flat.length; j++) pos.push(flat[j]);
    start[i + 1] = pos.length >> 1;
  }
  const polyPositions = new Float32Array(pos);
  const polyTriangles = readTriangles(table, start);
  return { polyPositions, polyStartIndices: start, vertexCount: polyPositions.length >> 1, count: n, polyTriangles };
}

/** The bake pre-tessellates every ring (see PolygonTriangulator) into per-row indices; flattening them
 *  to tile-global just adds each row's vertex start — one add per index, no geometry math. Handed to
 *  deck as its external `indices` buffer, which makes it skip the per-polygon earcut that used to run
 *  synchronously on the main thread at every tile load (the stutter that scaled with cell count). */
function readTriangles(table: Table, vertexStart: Uint32Array): Uint32Array | undefined {
  const tv = table.getChild(TileColumns.triangles);
  if (!tv) return undefined; // older bake — deck falls back to tessellating on the client
  const n = table.numRows;

  if (tv.data.length === 1) {
    const d = tv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Int32Array }[] };
    const off = d.valueOffsets;
    const flat = d.children[0].values;
    const out = new Uint32Array(off[n] - off[0]);
    let k = 0;
    for (let i = 0; i < n; i++) {
      const vs = vertexStart[i];
      for (let e = off[i]; e < off[i + 1]; e++) out[k++] = flat[e] + vs;
    }
    return out;
  }

  const acc: number[] = [];
  for (let i = 0; i < n; i++) {
    const sub = tv.get(i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    const vs = vertexStart[i];
    for (let j = 0; j < flat.length; j++) acc.push(flat[j] + vs);
  }
  return Uint32Array.from(acc);
}

/** Format-2 polygon geometry as views over the tile buffer: the coordinate child buffer viewed whole,
 *  the tile-global triangle indices reinterpreted in place (no per-row rebase — the bake already did it),
 *  and only the small (n+1) polyStartIndices built. */
function readPolygonsView(
  table: Table,
): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild(TileColumns.geometry);
  if (!gv) throw new Error('polygon tile has no geometry column');
  const n = table.numRows;
  const d = gv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array }[] };
  const offsets = d.valueOffsets; // float units into the child buffer
  if (offsets[0] !== 0) throw new Error('format 2: geometry child buffer is not zero-based');
  const polyPositions = d.children[0].values.subarray(0, offsets[n]); // view over this tile's coords
  const start = new Uint32Array(n + 1);
  for (let i = 0; i <= n; i++) start[i] = offsets[i] >> 1; // floats → vertices
  const polyTriangles = readTrianglesView(table);
  return { polyPositions, polyStartIndices: start, vertexCount: polyPositions.length >> 1, count: n, polyTriangles };
}

/** Format-2 triangle indices: one Uint32 view reinterpreting the Int32 child buffer (indices < 2^31,
 *  identical bytes). Already tile-global from the bake, so there is no rebase loop. */
function readTrianglesView(table: Table): Uint32Array | undefined {
  const tv = table.getChild(TileColumns.triangles);
  if (!tv) return undefined;
  const n = table.numRows;
  const d = tv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Int32Array }[] };
  const off = d.valueOffsets;
  const flat = d.children[0].values;
  return new Uint32Array(flat.buffer, flat.byteOffset + off[0] * 4, off[n] - off[0]);
}
