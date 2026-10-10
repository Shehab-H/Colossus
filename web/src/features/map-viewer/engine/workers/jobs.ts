import type { ViewConfig } from '@/types/viewConfig';
import type { FilterSlots } from '../filters/gpuFilterSlots';
import { type TileData, tileBytes, type TileColumn } from '../decode/tileData';
import { fetchArrowTable, fetchArrowBlock } from '../network/arrowFetch';
import { tileUrl, factsUrl } from '../network/tileUrls';
import { timedSync } from '@/lib/perf/timing';
import { decodeTile, decodeTileBlocks } from '../decode/decodeTile';
import { type RenderFetch, fetchBlocks, fetchSlabCellRows, fetchSlabPlanes } from '../network/packFetch';
import { fromBlocks } from '../decode/columnSource';
import { viewColumn } from '../decode/columns';
import { type CompanionFetch, type Companion, decodeCompanion } from '../fold/companion';
import { assembleDenseSlab, decodeSlab } from '../fold/slab/slabDecode';

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
  return timedSync('decode.tile', () => decodeTile(view, table, slots, tileFormat, buffer), (d) => ({ n: d.count, bytes: tileBytes(d), key }));
}

/** Load a packed tile's first paint: geometry + the active colour channel + the filter slots. */
export async function loadPackedTile(
  view: ViewConfig,
  spec: RenderFetch,
  slots: FilterSlots | null,
  key: string,
  signal?: AbortSignal,
): Promise<TileData> {
  const { tables, buffers } = await fetchBlocks(spec, signal);
  return timedSync('decode.tile', () => decodeTileBlocks(view, tables, buffers, slots), (d) => ({
    n: d.count,
    bytes: tileBytes(d),
    key,
  }));
}

/** One resident tile's extra columns, fetched on demand — a colour switch away from the default, or an
 *  inspect click. Returns just the decoded columns plus the buffers backing them, so the caller merges
 *  them into the existing tile entry rather than replacing it (cache identity stays (version, tileKey)). */
export interface LazyColumns {
  values: Record<string, TileColumn>;
  buffers: Record<string, ArrayBuffer>;
}

export async function loadColumns(
  view: ViewConfig,
  spec: RenderFetch,
  signal?: AbortSignal,
): Promise<LazyColumns> {
  const { tables, buffers } = await fetchBlocks(spec, signal);
  const src = fromBlocks(tables);
  const specByName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  const values: Record<string, TileColumn> = {};
  for (const group of Object.keys(tables)) {
    const col = src.column(group);
    if (col) values[group] = viewColumn(col, group, specByName.get(group), src.numRows);
  }
  return { values, buffers };
}

/** Fetch + decode a tile's fact companion into the shape its fold reads. */
export async function loadCompanion(spec: CompanionFetch, signal?: AbortSignal): Promise<Companion> {
  if (spec.kind === 'slab') {
    if (spec.slice) {
      // Dense cell-run slice (R5): fetch only the cell rows the context reads, assemble into a partial plane.
      const fetched = await fetchSlabCellRows(spec.baseUrl, spec.codec, spec.dir, spec.slice.sliceDir, spec.slice.cells, signal, spec.dictUrl);
      const data = timedSync('decode.companion', () => assembleDenseSlab(fetched, spec.slab, spec.markCount), (d) => ({ n: d.markCount, bytes: d.decodedBytes }));
      return { kind: 'slab', data };
    }
    const blocks = await fetchSlabPlanes(spec.baseUrl, spec.codec, spec.dir, spec.want, signal, spec.dictUrl);
    const data = timedSync('decode.companion', () => decodeSlab(blocks, spec.slab, spec.layout), (d) => ({ n: d.markCount, bytes: d.decodedBytes }));
    return { kind: 'slab', data };
  }
  const [z, x, y] = spec.key.split('/').map(Number);
  const { table } = spec.pack
    ? await fetchArrowBlock(spec.pack.url, spec.pack.offset, spec.pack.length, spec.pack.codec, signal)
    : await fetchArrowTable(factsUrl(spec.viewId, spec.version, z, x, y), signal);
  const data = timedSync('decode.companion', () => decodeCompanion(table, spec.grain), (d) => ({ n: d.rowCount, key: spec.key }));
  return { kind: 'row', data };
}
