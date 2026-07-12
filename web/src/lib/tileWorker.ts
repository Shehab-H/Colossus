// Off-main-thread tile decode. The zoom-swap freeze was fetch + Arrow parse + column materialization
// running synchronously on the main thread as a whole new tile set arrives. Here that runs in a worker,
// and every column is (or rides in) a typed array, so results transfer back zero-copy — the main thread
// never deserializes row-wise strings. A cancel message aborts the fetch; a tile already past fetch is
// decoded and kept (the bytes are paid for, and it may be wanted again on zoom-back).
import { loadTile, type TileData } from './tileData';
import type { ViewConfig } from './manifest';
import type { FilterSlots } from './gpuFilter';

interface LoadRequest {
  id: number;
  view: ViewConfig;
  version: string;
  key: string;
  slots: FilterSlots | null;
  tileFormat: number;
}

interface CancelRequest {
  cancel: number;
}

// Typed as a minimal dedicated-worker surface so the app tsconfig needn't pull in the webworker lib.
const ctx = self as unknown as {
  onmessage: ((e: MessageEvent<LoadRequest | CancelRequest>) => void) | null;
  postMessage: (message: unknown, transfer?: Transferable[]) => void;
};

// Every geometry/measure/code buffer moves (not copies) across the boundary; only a dict column's small
// dictionary and the rare string[] fallback are cloned.
function transferable(tile: TileData): Transferable[] {
  const buffers = new Set<ArrayBuffer>();
  const add = (v: ArrayBufferView | undefined) => {
    if (v) buffers.add(v.buffer as ArrayBuffer);
  };
  // Format 2: the retained buffer; every view into it dedups against this entry in the Set.
  if (tile.buffer) buffers.add(tile.buffer);
  add(tile.positions);
  add(tile.polyPositions);
  add(tile.polyStartIndices);
  add(tile.polyTriangles);
  add(tile.filterValues);
  for (const col of Object.values(tile.values)) {
    if (col instanceof Float32Array) add(col);
    else if (Array.isArray(col)) continue;
    else if (col.kind === 'dict') add(col.codes);
    else {
      add(col.bytes);
      add(col.offsets);
    }
  }
  return [...buffers];
}

const inflight = new Map<number, AbortController>();

ctx.onmessage = async (e) => {
  if ('cancel' in e.data) {
    inflight.get(e.data.cancel)?.abort();
    return;
  }
  const { id, view, version, key, slots, tileFormat } = e.data;
  const ac = new AbortController();
  inflight.set(id, ac);
  try {
    const tile = await loadTile(view, version, key, slots, tileFormat, ac.signal);
    ctx.postMessage({ id, tile }, transferable(tile));
  } catch (err) {
    const aborted = ac.signal.aborted;
    ctx.postMessage({ id, error: err instanceof Error ? err.message : String(err), aborted });
  } finally {
    inflight.delete(id);
  }
};
