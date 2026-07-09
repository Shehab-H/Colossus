// Off-main-thread tile decode. The zoom-swap freeze was fetch + Arrow parse + column materialization
// (position/color arrays and up to ~1M string values per tile) running synchronously on the main thread
// as a whole new tile set arrives. Here that runs in a worker; the decoded typed-array buffers transfer
// back zero-copy, so panning/zooming never blocks on a tile. String columns are structured-cloned.
import { loadTile, type TileData } from './tileData';
import type { ViewConfig } from './manifest';

interface LoadRequest {
  id: number;
  view: ViewConfig;
  version: string;
  key: string;
  filterSql: string;
}

// Typed as a minimal dedicated-worker surface so the app tsconfig needn't pull in the webworker lib.
const ctx = self as unknown as {
  onmessage: ((e: MessageEvent<LoadRequest>) => void) | null;
  postMessage: (message: unknown, transfer?: Transferable[]) => void;
};

// Every geometry/measure buffer can move (not copy) across the boundary; only string columns are cloned.
function transferable(tile: TileData): Transferable[] {
  const buffers = new Set<ArrayBuffer>();
  const add = (v: ArrayLike<number | string> | undefined) => {
    if (v && ArrayBuffer.isView(v)) buffers.add((v as ArrayBufferView).buffer as ArrayBuffer);
  };
  add(tile.positions);
  add(tile.polyPositions);
  add(tile.polyStartIndices);
  add(tile.polyTriangles);
  for (const col of Object.values(tile.values)) add(col);
  return [...buffers];
}

ctx.onmessage = async (e) => {
  const { id, view, version, key, filterSql } = e.data;
  try {
    const tile = await loadTile(view, version, key, filterSql);
    ctx.postMessage({ id, tile }, transferable(tile));
  } catch (err) {
    ctx.postMessage({ id, error: err instanceof Error ? err.message : String(err) });
  }
};
