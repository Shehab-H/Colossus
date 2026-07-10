import { tileBytes, type TileData } from './tileData';

const RETRY_MS = 2000;
// Resident-tile budget. Byte-based, not count-based: tile weight varies ~100× across datasets (a
// GeoNames point tile ≈ 12MB decoded), and a count cap let the heap run to gigabytes.
const BUDGET_BYTES = 384 * 1024 * 1024;

/** An immutable view of the cache. Its reference changes on every mutation (load, eviction, failure,
 *  retry, abort), which is exactly what React's useSyncExternalStore needs to decide when to re-render —
 *  and what lets the consumer derive from `tiles`/`error` directly, with no separate trigger dependency. */
export interface TileSnapshot {
  tiles: ReadonlyMap<string, TileData>;
  error: string | null;
}

/** What ensure() starts: a promise plus a cancel that aborts the underlying fetch. */
export interface CancellableLoad {
  promise: Promise<TileData>;
  cancel: () => void;
}

/** A framework-free, bounded tile store. Owns fetch de-duplication, cancellation, failure back-off,
 *  byte-budgeted pruning, and change notification; the hook on top stays declarative. Keys are opaque
 *  composite strings (version + filter + tile) so a manifest or filter switch can never collide with or
 *  resurrect stale rows. Every change publishes a fresh {@link TileSnapshot}. */
export class TileCache {
  private snapshot: TileSnapshot = { tiles: new Map(), error: null };
  private readonly loading = new Map<string, () => void>(); // key → cancel
  private readonly failedAt = new Map<string, number>();
  private readonly sizes = new Map<string, number>();
  private readonly listeners = new Set<() => void>();

  // Bound so they can be passed straight to useSyncExternalStore.
  subscribe = (onChange: () => void): (() => void) => {
    this.listeners.add(onChange);
    return () => this.listeners.delete(onChange);
  };
  getSnapshot = (): TileSnapshot => this.snapshot;

  has(key: string): boolean {
    return this.snapshot.tiles.has(key);
  }

  /** Fetch `key` if it isn't resident, in flight, or in failure back-off. `keepActive` returns the set
   *  of keys that must survive pruning (the on-screen selection + its cover) — evaluated after the load
   *  so cover stand-ins aren't evicted mid-transition. */
  ensure(key: string, start: () => CancellableLoad, keepActive: () => Set<string>): void {
    if (this.snapshot.tiles.has(key) || this.loading.has(key)) return;
    if (Date.now() - (this.failedAt.get(key) ?? -Infinity) < RETRY_MS) return;

    const load = start();
    this.loading.set(key, load.cancel);
    load.promise
      .then((tile) => {
        this.loading.delete(key);
        this.failedAt.delete(key);
        const next = new Map(this.snapshot.tiles);
        next.set(key, tile);
        this.sizes.set(key, tileBytes(tile));
        this.commit(this.prune(next, keepActive()), null);
      })
      .catch((e: unknown) => {
        this.loading.delete(key);
        if (e instanceof Error && e.name === 'AbortError') {
          // Cancelled, not failed: no back-off. Re-publish so the driving effect re-runs — if the key
          // became wanted again while the abort was in flight, that run re-requests it immediately.
          this.commit(this.snapshot.tiles, this.snapshot.error);
          return;
        }
        this.failedAt.set(key, Date.now());
        this.commit(this.snapshot.tiles, e instanceof Error ? e.message : String(e));
        // Re-publish after the back-off so the driving effect re-runs and retries.
        setTimeout(() => this.commit(this.snapshot.tiles, this.snapshot.error), RETRY_MS);
      });
  }

  /** Cancel in-flight loads for keys outside `active` — the load-shedding that stops a fast wheel-zoom
   *  from decoding every intermediate level's tiles after they've already left the screen. */
  abortStale(active: Set<string>): void {
    for (const [key, cancel] of this.loading) if (!active.has(key)) cancel();
  }

  private commit(tiles: ReadonlyMap<string, TileData>, error: string | null): void {
    this.snapshot = { tiles, error };
    for (const l of this.listeners) l();
  }

  /** Evict oldest-first (insertion order) past the byte budget, never touching `active` keys. Evicted
   *  tiles re-fetch from the browser's HTTP cache (tiles are immutable), so the budget trades a little
   *  re-decode on backtrack for a bounded resident heap. */
  private prune(tiles: Map<string, TileData>, active: Set<string>): Map<string, TileData> {
    let total = 0;
    for (const key of tiles.keys()) total += this.sizes.get(key) ?? 0;
    if (total <= BUDGET_BYTES) return tiles;
    for (const key of tiles.keys()) {
      if (total <= BUDGET_BYTES) break;
      if (active.has(key)) continue;
      total -= this.sizes.get(key) ?? 0;
      tiles.delete(key);
      this.sizes.delete(key);
    }
    return tiles;
  }
}
