// Off-main-thread tile decode. The zoom-swap freeze was fetch + Arrow parse + column materialization
// running synchronously on the main thread as a whole new tile set arrives. Here that runs in a worker,
// and every column is (or rides in) a typed array, so results transfer back zero-copy — the main thread
// never deserializes row-wise strings. A cancel message aborts the fetch; a tile already past fetch is
// decoded and kept (the bytes are paid for, and it may be wanted again on zoom-back).
import { type CompanionGrain, loadCompanion, loadTile, type PackBlock, type TileData } from './tileData';
import type { CompanionData } from './measures';
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

/** Fetch + decode a fact companion off the main thread — a low-zoom companion runs to millions of
 *  rows, and its typed columns transfer back zero-copy. `pack` (a packed leaf's block) routes the
 *  fetch to a range read of the archive; null keeps the per-file .facts.arrow fetch. */
interface CompanionRequest {
  id: number;
  companion: { viewId: string; version: string; key: string; grain: CompanionGrain[]; pack: PackBlock | null };
}

interface CancelRequest {
  cancel: number;
}

// Typed as a minimal dedicated-worker surface so the app tsconfig needn't pull in the webworker lib.
const ctx = self as unknown as {
  onmessage: ((e: MessageEvent<LoadRequest | CompanionRequest | CancelRequest>) => void) | null;
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

// The companion's typed columns, deduplicated to their backing buffers.
function companionTransferable(c: CompanionData): Transferable[] {
  const buffers = new Set<ArrayBuffer>();
  buffers.add(c.mki.buffer as ArrayBuffer);
  for (const d of Object.values(c.dim)) buffers.add(d.codes.buffer as ArrayBuffer);
  for (const t of Object.values(c.temporalDays)) buffers.add(t.buffer as ArrayBuffer);
  for (const p of Object.values(c.partial)) buffers.add(p.buffer as ArrayBuffer);
  return [...buffers];
}

const inflight = new Map<number, AbortController>();

ctx.onmessage = async (e) => {
  if ('cancel' in e.data) {
    inflight.get(e.data.cancel)?.abort();
    return;
  }
  const { id } = e.data;
  const ac = new AbortController();
  inflight.set(id, ac);
  try {
    if ('companion' in e.data) {
      const { viewId, version, key, grain, pack } = e.data.companion;
      const companion = await loadCompanion(viewId, version, key, grain, pack, ac.signal);
      ctx.postMessage({ id, companion }, companionTransferable(companion));
    } else {
      const { view, version, key, slots, tileFormat } = e.data;
      const tile = await loadTile(view, version, key, slots, tileFormat, ac.signal);
      ctx.postMessage({ id, tile }, transferable(tile));
    }
  } catch (err) {
    const aborted = ac.signal.aborted;
    ctx.postMessage({ id, error: err instanceof Error ? err.message : String(err), aborted });
  } finally {
    inflight.delete(id);
  }
};
