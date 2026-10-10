import type { Manifest, PackCodec, CompanionSlab } from '@/types/manifest';
import { NUMERIC_TYPES } from '../view/channelRoles';
import { slotNumericValues, arrowDict } from '../decode/filterValues';
import { dictFromArrow, plainString, numericColumn, packCodes } from '../decode/columns';
import type { PackBlock } from '../network/packFetch';
import type { SlabData } from './slab/slabDecode';
import { type Table, type Vector, Type } from 'apache-arrow';
import { dayNumber } from '../filters/filterValue';

/** One grain dimension of a companion: per-row codes into its small dictionary (written in the canonical
 *  order at bake). Codes stay integers through the whole fold — values only exist to resolve a context
 *  selection or a `where` literal to a code, once per fold. */
export interface CompanionDim {
  codes: Uint8Array | Uint16Array | Uint32Array;
  dict: string[];
}

/** A decoded fact companion, fully typed: `mki` keys each row to its mark's row index in the render
 *  tile (the bake writes it — no client-side string join), grain dimensions are dict codes, grain
 *  temporal values are day numbers, and partials are f32. Every array transfers across the worker
 *  boundary; nothing here allocates per-row strings. */
export interface CompanionData {
  rowCount: number;
  mki: Int32Array;
  dim: Record<string, CompanionDim>;
  temporalDays: Record<string, Float32Array>;
  numericValues?: Record<string, Float32Array>;
  partial: Record<string, Float32Array>;
}

/** Which companion columns are grain (and their temporality) — the minimal, structured-clone-friendly
 *  slice of the manifest the worker needs to decode a companion. */
export interface CompanionGrain {
  name: string;
  temporal: boolean;
  numeric: boolean;
}

export const companionGrain = (manifest: Manifest): CompanionGrain[] =>
  (manifest.grainChannels ?? []).map((g) => {
    const ch = manifest.view.source.channels.find((c) => c.name === g);
    return {
      name: g,
      temporal: ch?.role === 'temporal' || ch?.type === 'date',
      numeric: !!ch && NUMERIC_TYPES.has(ch.type),
    };
  });

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
  const numericValues: Record<string, Float32Array> = {};
  for (const g of grain) {
    const col = table.getChild(g.name);
    if (!col) continue;
    if (g.temporal) temporalDays[g.name] = temporalDaysColumn(col, n);
    else if (g.numeric) numericValues[g.name] = slotNumericValues(col, n);
    else dim[g.name] = dictFromArrow(col, plainString) ?? dictByScan(col, n);
  }

  const skip = new Set<string>([...grain.map((g) => g.name), 'mki']);
  const partial: Record<string, Float32Array> = {};
  for (const field of table.schema.fields) {
    if (skip.has(field.name)) continue;
    const col = table.getChild(field.name);
    if (col) partial[field.name] = numericColumn(col);
  }
  return { rowCount: n, mki, dim, temporalDays, numericValues, partial };
}

/** How to fetch one tile's companion, resolved on the main thread and shipped to the worker: a slab tile
 *  ranges its active planes out of the pack (R1/R5); a row-form tile ranges its block (R2) or fetches the
 *  per-file companion (internal levels, older bakes). */
export type CompanionFetch =
  | { kind: 'row'; viewId: string; version: string; key: string; grain: CompanionGrain[]; pack: PackBlock | null }
  | {
      kind: 'slab';
      baseUrl: string;
      codec: PackCodec;
      /** The pack's trained zstd dictionary URL (Work Item C), or undefined (gzip / no dict). */
      dictUrl?: string;
      slab: CompanionSlab;
      /** The tile's resolved layout (SLAB-FORMAT §3) — decode branches on this, not the view default. */
      layout: 'sparse' | 'dense';
      markCount: number;
      dir: Record<string, [number, number]>;
      /** Sparse / dense-whole: the plane names to fetch whole. */
      want: string[];
      /** Dense cell-run slice (R5): fetch only these cell rows per plane instead of whole planes. */
      slice?: { sliceDir: Record<string, number[]>; cells: Record<string, number[]> };
    };

/** A decoded companion, tagged by format so the fold picks foldTile (row) or foldSlab (slab). */
export type Companion = { kind: 'row'; data: CompanionData } | { kind: 'slab'; data: SlabData };

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
