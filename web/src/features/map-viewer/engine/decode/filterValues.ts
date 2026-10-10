import type { ColumnSource } from './columnSource';
import { type FilterSlots, MISSING_CODE, buildFilterValues, canonicalCodeLut } from '../filters/gpuFilterSlots';
import { type Vector, Type } from 'apache-arrow';
import { dayNumber } from '../filters/filterValue';

/** Per-tile GPU filter attribute: one float slot per filterable channel per mark (points) or vertex
 *  (polygons). Temporal slots hold day numbers; dimension slots hold canonical category codes, remapped
 *  from the tile-local dictionary so the code space is one per (view, version). */
export function buildSlotValues(
  src: ColumnSource,
  slots: FilterSlots | null | undefined,
  count: number,
  polyStartIndices?: Uint32Array,
  vertexCount?: number,
): Float32Array | undefined {
  if (!slots) return undefined;
  const n = src.numRows;
  const perMark = slots.specs.map((spec) => {
    const col = src.column(spec.name);
    if (!col) return new Float32Array(n).fill(MISSING_CODE); // tile lacks the column → kept by (all), matched by nothing
    if (spec.kind === 'temporal') return slotDayNumbers(col, n);
    if (spec.kind === 'numeric') return slotNumericValues(col, n);
    return slotCanonicalCodes(col, n, spec.categories);
  });
  return buildFilterValues(slots.size, count, perMark, polyStartIndices, vertexCount);
}

/** Single-chunk Arrow dictionary access (the index buffer IS the code space; the dictionary holds the
 *  logical values) — lets a slot decode the small dictionary once and gather codes, never a per-row scan. */
export function arrowDict(col: Vector): { codes: ArrayLike<number>; dict: Vector } | null {
  if (col.type.typeId !== Type.Dictionary || col.data.length !== 1 || col.nullCount > 0) return null;
  const d = col.data[0] as unknown as { values: ArrayLike<number>; dictionary?: Vector };
  return d.dictionary ? { codes: d.values, dict: d.dictionary } : null;
}

/** Per-mark day numbers for a temporal slot (mirrors filterValue.dayNumber). */
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

/** Per-mark numeric values for a range slot. Missing and non-finite values use the same sentinel as a
 *  missing categorical value: `(all)` keeps them, while a real range bounded by the baked domain does
 *  not accidentally match them. */
export function slotNumericValues(col: Vector, n: number): Float32Array {
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const raw = col.get(i);
    const value = raw === null || raw === undefined ? NaN : Number(raw);
    out[i] = Number.isFinite(value) ? value : MISSING_CODE;
  }
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
