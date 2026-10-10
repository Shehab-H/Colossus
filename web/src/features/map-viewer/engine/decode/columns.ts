import type { ViewConfig, ChannelSpec } from '@/types/viewConfig';
import type { TileColumn, DictColumn, Utf8Column } from './tileData';
import { NUMERIC_TYPES, measureChannels, isGroupRegime } from '../view/channelRoles';
import { TileColumns } from './tileSchema';
import type { ColumnSource } from './columnSource';
import { type Table, type Vector, Type } from 'apache-arrow';

/** Pull the columns we actually use out of the tile into standalone columns: every measure (kept numeric
 *  for the color ramp) plus any channel named by `inspect`. We COPY rather than hold Arrow's view — a
 *  view keeps the whole tile message alive (all columns + Arrow overhead), so copying only what we need
 *  and letting the Table be GC'd is what keeps the heap bounded. */
export function readFields(view: ViewConfig, table: Table): Record<string, TileColumn> {
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
export function readFieldsView(view: ViewConfig, src: ColumnSource): Record<string, TileColumn> {
  const { specByName, names } = fieldSelection(view);
  const values: Record<string, TileColumn> = {};
  for (const name of names) {
    const col = src.column(name);
    if (!col) continue; // not fetched yet (packed tile, lazy column) — simply absent from `values`
    values[name] = viewColumn(col, name, specByName.get(name), src.numRows);
  }
  return values;
}

/** One column in its zero-copy view form, typed by its declared spec. Split out of readFieldsView so a
 *  lazily fetched block decodes into the exact same shape before being merged into a resident tile. */
export function viewColumn(col: Vector, name: string, spec: ChannelSpec | undefined, n: number): TileColumn {
  const temporal = spec?.role === 'temporal' || spec?.type === 'date';
  return NUMERIC_TYPES.has(spec?.type ?? '')
    ? viewFloat32(col, name)
    : stringColumnView(col, spec?.role === 'identity', n, temporal ? isoDate : plainString);
}

/** A single-chunk f32 Arrow column as a view over the tile buffer, bounded to its logical rows (Arrow
 *  pads buffers, so the raw `values` can be longer). Format 2 casts every measure to REAL at bake, so
 *  this must succeed; a non-f32 or chunked/null column is a contract violation, thrown so a bad bake
 *  fails visibly rather than silently degrading to a copy. */
export function viewFloat32(col: Vector, name: string): Float32Array {
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

export const plainString = (v: unknown): string => String(v);

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

export function numericColumn(col: Vector): Float32Array {
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

export const packCodes = (codes: Uint32Array, cardinality: number): DictColumn['codes'] =>
  cardinality <= 256 ? Uint8Array.from(codes) : cardinality <= 65536 ? Uint16Array.from(codes) : codes;

/** Arrow dictionary-encoded column: the indices ARE the codes — no per-row scan at all; only the small
 *  dictionary itself goes through the normalizer. */
export function dictFromArrow(col: Vector, norm: (v: unknown) => string): DictColumn | null {
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
