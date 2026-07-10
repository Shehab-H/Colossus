import { Type, type Table, type Vector } from 'apache-arrow';
import type { ViewConfig } from './manifest';
import { tileUrl } from './manifest';
import { fetchArrowTable } from './arrow';
import { measureChannels, NUMERIC_TYPES, parseDateRange, inDateRange } from './channels';
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
 *  moves and measure switches); the per-mark channel values ride along so recoloring — and answering a
 *  click — is a client-side scan, not a re-fetch. Colors are derived at layer time (see lib/deckData). */
export interface TileData {
  count: number; // marks in the tile (polygons or points)
  // Polygon marks: deck SolidPolygonLayer binary layout, mostly zero-copy from Arrow's list buffers.
  polyPositions?: Float32Array; // flat [x0,y0,x1,y1,…], every ring concatenated
  polyStartIndices?: Uint32Array; // vertex offset per polygon; length = count + 1
  vertexCount?: number; // total vertices across all polygons
  polyTriangles?: Uint32Array; // bake-time triangle indices (tile-global) — deck skips earcut entirely
  // Point marks:
  positions?: Float32Array; // interleaved [x0,y0,…]
  // One column per measure plus any channel named by `inspect`, keyed by channel name.
  values: Record<string, TileColumn>;
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

/** Approximate resident bytes of a tile — what the cache budgets against (see TileCache). */
export function tileBytes(d: TileData): number {
  let b =
    (d.positions?.byteLength ?? 0) +
    (d.polyPositions?.byteLength ?? 0) +
    (d.polyStartIndices?.byteLength ?? 0) +
    (d.polyTriangles?.byteLength ?? 0);
  for (const col of Object.values(d.values)) {
    if (col instanceof Float32Array) b += col.byteLength;
    else if (Array.isArray(col)) b += col.length * 24; // rough JS string+slot overhead
    else if (col.kind === 'dict') b += col.codes.byteLength + col.dict.length * 16;
    else b += col.bytes.byteLength + col.offsets.byteLength;
  }
  return b;
}

export async function loadTile(
  view: ViewConfig,
  version: string,
  key: string,
  filters: Record<string, string>,
  signal?: AbortSignal,
): Promise<TileData> {
  const [z, x, y] = key.split('/').map(Number);
  const table = await fetchArrowTable(tileUrl(view.id, version, z, x, y), signal);
  return decodeTile(view, table, filters);
}

/** Arrow table → TileData. Pure (no fetch) so the decode is unit-testable. Active filters drop
 *  non-matching rows here, in the worker, before any geometry or column is materialized. */
export function decodeTile(view: ViewConfig, table: Table, filters?: Record<string, string>): TileData {
  const keep = filters ? rowsMatching(view, table, filters) : null;
  const values = readFields(view, table, keep);

  if (view.mark === 'polygon') return { ...readPolygons(table, keep), values };

  const xs = table.getChild(TileColumns.x)!.toArray() as ArrayLike<number>;
  const ys = table.getChild(TileColumns.y)!.toArray() as ArrayLike<number>;
  const n = keep ? keep.length : xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    const r = keep ? keep[i] : i;
    positions[i * 2] = xs[r];
    positions[i * 2 + 1] = ys[r];
  }
  return { count: n, positions, values };
}

/** Row indices matching every filter, or null for "all rows" (null preserves the zero-copy decode
 *  paths). Values compare in the same normalized string form the filter options use (temporal →
 *  YYYY-MM-DD); dictionary columns match on integer codes, never per-row strings. */
function rowsMatching(view: ViewConfig, table: Table, filters: Record<string, string>): Uint32Array | null {
  const entries = Object.entries(filters);
  if (entries.length === 0) return null;
  const specByName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  const n = table.numRows;
  const mask = new Uint8Array(n).fill(1);

  for (const [name, value] of entries) {
    const col = table.getChild(name);
    if (!col) return new Uint32Array(0); // can't evaluate the predicate — match nothing rather than lie
    const spec = specByName.get(name);
    const temporal = spec?.role === 'temporal' || spec?.type === 'date';
    const norm = temporal ? isoDate : plainString;
    const range = temporal ? parseDateRange(value) : null;
    if (temporal && !range) continue; // temporal value with no real bounds → not a predicate
    const test = range ? (s: string) => inDateRange(s, range) : (s: string) => s === value;

    const d =
      col.type.typeId === Type.Dictionary && col.data.length === 1 && col.nullCount === 0
        ? (col.data[0] as unknown as { values: ArrayLike<number>; dictionary?: Vector })
        : null;
    if (d?.dictionary) {
      const dv = d.dictionary;
      const hit = new Uint8Array(dv.length);
      for (let c = 0; c < dv.length; c++) if (test(norm(dv.get(c)))) hit[c] = 1;
      for (let i = 0; i < n; i++) if (mask[i] && hit[d.values[i]] === 0) mask[i] = 0;
    } else {
      for (let i = 0; i < n; i++) if (mask[i] && !test(norm(col.get(i)))) mask[i] = 0;
    }
  }

  let m = 0;
  for (let i = 0; i < n; i++) m += mask[i];
  if (m === n) return null;
  const keep = new Uint32Array(m);
  for (let i = 0, k = 0; i < n; i++) if (mask[i]) keep[k++] = i;
  return keep;
}

/** Pull the columns we actually use out of the tile into standalone columns: every measure (kept numeric
 *  for the color ramp) plus any channel named by `inspect`. We COPY rather than hold Arrow's view — a
 *  view keeps the whole tile message alive (all columns + Arrow overhead), so copying only what we need
 *  and letting the Table be GC'd is what keeps the heap bounded. */
function readFields(view: ViewConfig, table: Table, keep: Uint32Array | null): Record<string, TileColumn> {
  const specByName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  const names = new Set<string>();
  for (const ch of measureChannels(view)) names.add(ch.name);
  if (view.encoding?.color?.channel) names.add(view.encoding.color.channel); // color may be a dimension
  for (const name of view.inspect?.channels ?? []) names.add(name);
  if (view.inspect?.title) names.add(view.inspect.title);

  const values: Record<string, TileColumn> = {};
  for (const name of names) {
    const col = table.getChild(name);
    if (!col) continue;
    const spec = specByName.get(name);
    const temporal = spec?.role === 'temporal' || spec?.type === 'date';
    values[name] = NUMERIC_TYPES.has(spec?.type ?? '')
      ? numericColumn(col, keep)
      : stringColumn(col, spec?.role === 'identity', table.numRows, temporal ? isoDate : plainString, keep);
  }
  return values;
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

function numericColumn(col: Vector, keep: Uint32Array | null): Float32Array {
  const a = col.toArray() as ArrayLike<number>;
  if (!keep) return a instanceof Float32Array ? a.slice() : Float32Array.from(a);
  const out = new Float32Array(keep.length);
  for (let i = 0; i < keep.length; i++) out[i] = a[keep[i]];
  return out;
}

// Past this many distinct values a column isn't categorical in any useful sense — stop scanning.
const DICT_CAP = 65536;

/** Non-numeric column → the cheapest faithful form. Identity channels (per-row unique by design, e.g. a
 *  place name) skip straight to raw UTF-8; everything else dict-codes, bailing to raw UTF-8 if the
 *  cardinality cap is hit. All row-wise string work happens here, in the worker, where strings die young. */
function stringColumn(
  col: Vector,
  identity: boolean,
  n: number,
  norm: (v: unknown) => string,
  keep: Uint32Array | null,
): TileColumn {
  if (identity) {
    const u = utf8Column(col, n, keep);
    if (u) return u;
  }
  const direct = dictFromArrow(col, n, norm, keep);
  if (direct) return direct;

  const m = keep ? keep.length : n;
  const codeOf = new Map<string, number>();
  const dict: string[] = [];
  const codes = new Uint32Array(m);
  for (let i = 0; i < m; i++) {
    const s = norm(col.get(keep ? keep[i] : i));
    let c = codeOf.get(s);
    if (c === undefined) {
      if (dict.length >= DICT_CAP) return utf8Column(col, n, keep) ?? materializeStrings(col, n, norm, keep);
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
 *  dictionary itself goes through the normalizer. Filtered rows gather codes; the dictionary stays whole
 *  so codes (and category colors) are identical filtered or not. */
function dictFromArrow(col: Vector, n: number, norm: (v: unknown) => string, keep: Uint32Array | null): DictColumn | null {
  if (col.type.typeId !== Type.Dictionary || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { values: ArrayLike<number>; dictionary?: Vector };
  const dv = d.dictionary;
  if (!dv) return null;
  const dict = new Array<string>(dv.length);
  for (let i = 0; i < dv.length; i++) dict[i] = norm(dv.get(i));
  const m = keep ? keep.length : n;
  const codes = new Uint32Array(m);
  for (let i = 0; i < m; i++) codes[i] = d.values[keep ? keep[i] : i];
  return { kind: 'dict', codes: packCodes(codes, dict.length), dict };
}

/** Compact copy of a single-chunk UTF-8 column's raw buffers (offsets rebased to 0), gathering only kept
 *  rows when a filter is active. Declined for null-bearing columns — the dict/string paths preserve
 *  today's String(null) → "null" rendering. */
function utf8Column(col: Vector, n: number, keep: Uint32Array | null): Utf8Column | null {
  if (col.type.typeId !== Type.Utf8 || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { valueOffsets: Int32Array; values: Uint8Array };
  const off = d.valueOffsets;

  if (!keep) {
    const base = off[0];
    const bytes = d.values.slice(base, off[n]);
    const offsets = new Int32Array(n + 1);
    for (let i = 0; i <= n; i++) offsets[i] = off[i] - base;
    return { kind: 'utf8', bytes, offsets };
  }

  let total = 0;
  for (let i = 0; i < keep.length; i++) total += off[keep[i] + 1] - off[keep[i]];
  const bytes = new Uint8Array(total);
  const offsets = new Int32Array(keep.length + 1);
  let w = 0;
  for (let i = 0; i < keep.length; i++) {
    const a = off[keep[i]];
    const b = off[keep[i] + 1];
    bytes.set(d.values.subarray(a, b), w);
    w += b - a;
    offsets[i + 1] = w;
  }
  return { kind: 'utf8', bytes, offsets };
}

function materializeStrings(col: Vector, n: number, norm: (v: unknown) => string, keep: Uint32Array | null): string[] {
  const m = keep ? keep.length : n;
  const s = new Array<string>(m);
  for (let i = 0; i < m; i++) s[i] = norm(col.get(keep ? keep[i] : i));
  return s;
}

/** Extract deck's binary polygon layout from Arrow's `geometry` list column: one contiguous coordinate
 *  buffer + per-polygon vertex offsets. The coordinates are copied out (see readFields) so the Arrow
 *  message — including the `x`/`y`/`part_offsets` columns a polygon mark never uses — can be released. */
function readPolygons(
  table: Table,
  keep: Uint32Array | null,
): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild(TileColumns.geometry);
  if (!gv) throw new Error('polygon tile has no geometry column');
  const n = table.numRows;

  if (gv.data.length === 1) {
    const d = gv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array }[] };
    const offsets = d.valueOffsets; // element (float) units into the child buffer

    if (!keep) {
      const base = offsets[0]; // float offset of the first ring (0 for our freshly-written tiles)
      const positions = d.children[0].values.slice(base, offsets[n]); // compact copy of just this tile's coords
      const start = new Uint32Array(n + 1);
      for (let i = 0; i <= n; i++) start[i] = (offsets[i] - base) >> 1; // floats → vertices
      const polyTriangles = readTriangles(table, start, null);
      return { polyPositions: positions, polyStartIndices: start, vertexCount: positions.length >> 1, count: n, polyTriangles };
    }

    const m = keep.length;
    let total = 0;
    for (let i = 0; i < m; i++) total += offsets[keep[i] + 1] - offsets[keep[i]];
    const positions = new Float32Array(total);
    const start = new Uint32Array(m + 1);
    let w = 0;
    for (let i = 0; i < m; i++) {
      const a = offsets[keep[i]];
      const b = offsets[keep[i] + 1];
      positions.set(d.children[0].values.subarray(a, b), w);
      w += b - a;
      start[i + 1] = w >> 1;
    }
    const polyTriangles = readTriangles(table, start, keep);
    return { polyPositions: positions, polyStartIndices: start, vertexCount: positions.length >> 1, count: m, polyTriangles };
  }

  // Fallback (multi-chunk): concatenate ring by ring.
  const rows = keep ? keep.length : n;
  const pos: number[] = [];
  const start = new Uint32Array(rows + 1);
  for (let i = 0; i < rows; i++) {
    const sub = gv.get(keep ? keep[i] : i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    for (let j = 0; j < flat.length; j++) pos.push(flat[j]);
    start[i + 1] = pos.length >> 1;
  }
  const polyPositions = new Float32Array(pos);
  const polyTriangles = readTriangles(table, start, keep);
  return { polyPositions, polyStartIndices: start, vertexCount: polyPositions.length >> 1, count: rows, polyTriangles };
}

/** The bake pre-tessellates every ring (see PolygonTriangulator) into per-row indices; flattening them
 *  to tile-global just adds each row's vertex start — one add per index, no geometry math. Handed to
 *  deck as its external `indices` buffer, which makes it skip the per-polygon earcut that used to run
 *  synchronously on the main thread at every tile load (the stutter that scaled with cell count).
 *  `vertexStart` indexes OUTPUT rows, so filtered tiles re-base each kept row's indices correctly. */
function readTriangles(table: Table, vertexStart: Uint32Array, keep: Uint32Array | null): Uint32Array | undefined {
  const tv = table.getChild(TileColumns.triangles);
  if (!tv) return undefined; // older bake — deck falls back to tessellating on the client
  const n = table.numRows;
  const rows = keep ? keep.length : n;

  if (tv.data.length === 1) {
    const d = tv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Int32Array }[] };
    const off = d.valueOffsets;
    const flat = d.children[0].values;
    let total = 0;
    if (keep) for (let i = 0; i < rows; i++) total += off[keep[i] + 1] - off[keep[i]];
    else total = off[n] - off[0];
    const out = new Uint32Array(total);
    let k = 0;
    for (let i = 0; i < rows; i++) {
      const r = keep ? keep[i] : i;
      const vs = vertexStart[i];
      for (let e = off[r]; e < off[r + 1]; e++) out[k++] = flat[e] + vs;
    }
    return out;
  }

  const acc: number[] = [];
  for (let i = 0; i < rows; i++) {
    const sub = tv.get(keep ? keep[i] : i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    const vs = vertexStart[i];
    for (let j = 0; j < flat.length; j++) acc.push(flat[j] + vs);
  }
  return Uint32Array.from(acc);
}
