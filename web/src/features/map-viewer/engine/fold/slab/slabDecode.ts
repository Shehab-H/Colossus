import type { Manifest, SlabAxis, CompanionSlab } from '@/types/manifest';
import { computeStrides } from './slabAxes';
import { isScanPlane } from './slabPlanes';
import { type Table, tableFromIPC } from 'apache-arrow';

// Client slab reader + fold (docs/companion-scale/SLAB-FORMAT.md), the R1 counterpart to the row-form
// decodeCompanion/foldTile. Same frozen fold results (VIEW_CONFIG §4) — only the traversal differs:
// sparse CSR scans a per-cell predicate (reusing aggregators.ts InnerAgg for the accumulation +
// finalization); dense reads cumulative cell rows in O(1) per range. The manifest's `companionSlab` gates
// which path runs; the row form stays for older bakes.

export const isSlab = (m: Manifest): boolean => !!m.companionSlab;

type CellIds = Uint8Array | Uint16Array | Uint32Array;

/** A decoded slab tile: sparse carries `offsets`+`cellIds` plus per-partial planes over `nnz` entries;
 *  dense carries only cell-major (cumulative) planes over `cells × markCount`. Planes are Float32 (a cnt
 *  plane is widened from i32 at decode — counts are exact in f32). `strides` are the cell-order strides. */
export interface SlabData {
  layout: 'sparse' | 'dense';
  markCount: number;
  cells: number;
  axes: SlabAxis[];
  strides: number[];
  offsets?: Int32Array;
  cellIds?: CellIds;
  planes: Record<string, Float32Array>;
  /** Dense cell-run slicing (R5): which cell rows of each plane are materialized. A plane array is full
   *  length (`cells × markCount`) but only these rows carry real values (the rest are 0 / NaN); the fold
   *  reads only the rows a context needs, and an incremental fetch fills more (SLAB-FORMAT §5). Absent ⇒
   *  every cell is resident (a whole-plane decode — sparse, or an un-sliced dense fetch). */
  residentCells?: Record<string, Set<number>>;
  decodedBytes: number;
}

/** The single-row List column's child typed array, viewed in place (zero-copy). */
function listChild(table: Table, name: string): Float32Array | Int32Array | CellIds {
  const col = table.getChild(name);
  if (!col) throw new Error(`slab block missing column '${name}'`);
  const d = col.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array | Int32Array | CellIds }[] };
  const base = d.valueOffsets[0];
  const n = d.valueOffsets[1] - base;
  return d.children[0].values.subarray(base, base + n);
}

/** Decode a slab tile from a map of `planeName → decompressed Arrow IPC bytes`, under the tile's resolved
 *  layout (SLAB-FORMAT §3 — per tile, not per view). Only the planes present in the map are decoded — plane
 *  splitting hands in `@idx` (sparse) plus just the active measures' planes. */
export function decodeSlab(
  planeBlocks: Record<string, ArrayBuffer>,
  slab: CompanionSlab,
  layout: 'sparse' | 'dense' = slab.layout,
): SlabData {
  const strides = computeStrides(slab.axes);
  let offsets: Int32Array | undefined;
  let cellIds: CellIds | undefined;
  let bytes = 0;
  // `@idx` is absent when this is an incremental plane-split fetch (the structure was decoded on an
  // earlier fetch of the same tile and cached under its key); the caller merges these planes into it.
  if (layout === 'sparse' && planeBlocks['@idx']) {
    const idx = tableFromIPC(new Uint8Array(planeBlocks['@idx']));
    offsets = listChild(idx, 'offsets') as Int32Array;
    cellIds = listChild(idx, 'cellIds') as CellIds;
    bytes += offsets.byteLength + cellIds.byteLength;
  }
  const planes: Record<string, Float32Array> = {};
  for (const p of slab.partials) {
    const blk = planeBlocks[p.name];
    if (!blk) continue;
    const child = listChild(tableFromIPC(new Uint8Array(blk)), p.name);
    planes[p.name] = p.type === 'i32' ? Float32Array.from(child as Int32Array) : (child as Float32Array);
    bytes += planes[p.name].byteLength;
  }
  const first = Object.values(planes)[0];
  const markCount = offsets ? offsets.length - 1 : layout === 'dense' && first ? first.length / slab.cells : 0;
  return { layout, markCount, cells: slab.cells, axes: slab.axes, strides, offsets, cellIds, planes, decodedBytes: bytes };
}

/** Assemble fetched cell-row blocks into a dense `SlabData`. Each plane array is full length
 *  (`cells × markCount`) with only the fetched rows filled — 0 for additive planes, NaN for scan — and
 *  `residentCells` records which. The fold reads only the rows a context needs (denseNeeds), so the holes are
 *  never read; an incremental fetch fills more via mergeSlab. `fetched` is `plane → {cell, buf}[]` of raw
 *  little-endian blocks (i32 cnt widened to f32, as decodeSlab does). */
export function assembleDenseSlab(
  fetched: Record<string, { cell: number; buf: ArrayBuffer }[]>,
  slab: CompanionSlab,
  markCount: number,
): SlabData {
  const strides = computeStrides(slab.axes);
  const type = new Map(slab.partials.map((p) => [p.name, p.type]));
  const planes: Record<string, Float32Array> = {};
  const residentCells: Record<string, Set<number>> = {};
  let bytes = 0;
  for (const [name, rows] of Object.entries(fetched)) {
    const plane = new Float32Array(slab.cells * markCount);
    if (isScanPlane(name)) plane.fill(NaN);
    const resident = new Set<number>();
    for (const { cell, buf } of rows) {
      const row = type.get(name) === 'i32' ? Float32Array.from(new Int32Array(buf)) : new Float32Array(buf);
      plane.set(row, cell * markCount);
      resident.add(cell);
      bytes += buf.byteLength;
    }
    planes[name] = plane;
    residentCells[name] = resident;
  }
  return { layout: 'dense', markCount, cells: slab.cells, axes: slab.axes, strides, planes, residentCells, decodedBytes: bytes };
}

/** Merge a freshly-fetched companion `next` into a cached `prev` under the same tile key (SLAB-FORMAT §5).
 *  Dense: copy next's resident cell rows into prev's plane arrays (adding a plane prev lacked) and union the
 *  resident sets — an incremental slice fetch that a later context widened. Sparse: add whole planes (a plane
 *  split), the prior behaviour. */
export function mergeSlab(prev: SlabData, next: SlabData): void {
  if (prev.layout === 'dense' && next.layout === 'dense') {
    const M = prev.markCount;
    prev.residentCells ??= {};
    for (const [name, plane] of Object.entries(next.planes)) {
      const resident = next.residentCells?.[name];
      if (!prev.planes[name] || !resident) {
        prev.planes[name] = plane;
        prev.residentCells[name] = new Set(resident ?? []);
        continue;
      }
      const target = prev.planes[name];
      const set = (prev.residentCells[name] ??= new Set());
      for (const c of resident) {
        target.set(plane.subarray(c * M, (c + 1) * M), c * M);
        set.add(c);
      }
    }
  } else {
    Object.assign(prev.planes, next.planes);
  }
  prev.decodedBytes += next.decodedBytes;
}
