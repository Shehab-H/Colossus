import { type Companion, type CompanionFetch, loadCompanion, loadPackedTile, loadTile, type RenderFetch, type TileData } from './tileData';
import type { ViewConfig } from './manifest';
import type { FilterSlots } from './gpuFilter';
import { emitAll, isPerfOn, type PerfEvent } from './perf';

interface LoadResponse {
  id: number;
  tile?: TileData;
  companion?: Companion;
  error?: string;
  aborted?: boolean;
  /** Stage timings measured in the worker (fetch, inflate, decode) — re-emitted into the main-thread bus. */
  perf?: PerfEvent[];
}

interface Pending {
  resolve: (result: LoadResponse) => void;
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
    if (res.perf) emitAll(res.perf);
    const p = this.pending.get(res.id);
    if (!p) return;
    this.pending.delete(res.id);
    if (res.error) {
      const e = new Error(res.error);
      if (res.aborted) e.name = 'AbortError';
      p.reject(e);
    } else p.resolve(res);
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

  load(
    view: ViewConfig,
    version: string,
    key: string,
    slots: FilterSlots | null,
    tileFormat: number,
    render?: RenderFetch | null,
  ): TileLoad {
    this.ensure();
    if (!this.workers.length) {
      const ac = new AbortController();
      const promise = render
        ? loadPackedTile(view, render, slots, key, ac.signal)
        : loadTile(view, version, key, slots, tileFormat, ac.signal);
      return { promise, cancel: () => ac.abort() };
    }
    const id = this.nextId++;
    const worker = this.workers[this.rr++ % this.workers.length];
    const promise = new Promise<LoadResponse>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      worker.postMessage({ id, view, version, key, slots, tileFormat, render, perf: isPerfOn() });
    }).then((r) => r.tile as TileData);
    // postMessage on a terminated worker is a silent no-op, so a late cancel is always safe.
    return { promise, cancel: () => worker.postMessage({ cancel: id }) };
  }

  /** Fetch + decode a tile's fact companion on the worker pool (main-thread fallback when workers are
   *  unavailable). The typed columns/planes transfer back — the main thread never parses companion Arrow.
   *  `spec` routes a slab tile to its plane ranges (R1/R5) or a row-form tile to its block/per-file file. */
  loadCompanion(spec: CompanionFetch): Promise<Companion> {
    this.ensure();
    if (!this.workers.length) return loadCompanion(spec);
    const id = this.nextId++;
    const worker = this.workers[this.rr++ % this.workers.length];
    return new Promise<LoadResponse>((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      worker.postMessage({ id, companion: spec, perf: isPerfOn() });
    }).then((r) => r.companion as Companion);
  }
}

export const tileLoader = new TileLoader();
