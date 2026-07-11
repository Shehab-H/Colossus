import { loadTile, type TileData } from './tileData';
import type { ViewConfig } from './manifest';
import type { FilterSlots } from './gpuFilter';

interface LoadResponse {
  id: number;
  tile?: TileData;
  error?: string;
  aborted?: boolean;
}

interface Pending {
  resolve: (tile: TileData) => void;
  reject: (err: Error) => void;
}

/** A load in flight. `cancel` aborts the network fetch (see tileWorker); the promise then rejects with
 *  an AbortError-named error, which the cache treats as "not wanted" rather than "failed". */
export interface TileLoad {
  promise: Promise<TileData>;
  cancel: () => void;
}

/** A small pool of decode workers (see tileWorker). Tile fetch + Arrow parse + column building runs off
 *  the main thread, so a zoom-swap — a whole new tile set arriving at once — never freezes interaction;
 *  results transfer back zero-copy. Degrades to synchronous main-thread decode if workers are
 *  unavailable or a worker fails, so behavior is preserved everywhere. */
class TileLoader {
  private workers: Worker[] = [];
  private readonly pending = new Map<number, Pending>();
  private nextId = 0;
  private rr = 0;
  private disabled = false;

  private ensure(): void {
    if (this.workers.length || this.disabled) return;
    try {
      const n = Math.max(1, Math.min(navigator.hardwareConcurrency || 4, 4));
      for (let i = 0; i < n; i++) {
        const w = new Worker(new URL('./tileWorker.ts', import.meta.url), { type: 'module' });
        w.onmessage = (e: MessageEvent<LoadResponse>) => this.settle(e.data);
        w.onerror = () => this.fail();
        this.workers.push(w);
      }
    } catch {
      this.disabled = true; // no worker support in this environment
    }
  }

  private settle(res: LoadResponse): void {
    const p = this.pending.get(res.id);
    if (!p) return;
    this.pending.delete(res.id);
    if (res.error) {
      const e = new Error(res.error);
      if (res.aborted) e.name = 'AbortError';
      p.reject(e);
    } else p.resolve(res.tile as TileData);
  }

  // A worker died (e.g. module load failed): tear the pool down and reject in-flight loads. The cache's
  // back-off retries them, and load() now routes to the main-thread fallback.
  private fail(): void {
    this.disabled = true;
    for (const w of this.workers) w.terminate();
    this.workers = [];
    const inflight = [...this.pending.values()];
    this.pending.clear();
    for (const p of inflight) p.reject(new Error('tile worker failed'));
  }

  load(view: ViewConfig, version: string, key: string, slots: FilterSlots | null, tileFormat: number): TileLoad {
    this.ensure();
    if (!this.workers.length) {
      const ac = new AbortController();
      return { promise: loadTile(view, version, key, slots, tileFormat, ac.signal), cancel: () => ac.abort() };
    }
    const id = this.nextId++;
    const worker = this.workers[this.rr++ % this.workers.length];
    const promise = new Promise<TileData>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      worker.postMessage({ id, view, version, key, slots, tileFormat });
    });
    // postMessage on a terminated worker is a silent no-op, so a late cancel is always safe.
    return { promise, cancel: () => worker.postMessage({ cancel: id }) };
  }
}

export const tileLoader = new TileLoader();
