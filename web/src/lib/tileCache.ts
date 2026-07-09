import type { TileData } from './tileData';

const RETRY_MS = 2000;
const CAP = 64;

/** An immutable view of the cache. Its reference changes on every mutation (load, eviction, failure,
 *  retry), which is exactly what React's useSyncExternalStore needs to decide when to re-render — and
 *  what lets the consumer derive from `tiles`/`error` directly, with no separate trigger dependency. */
export interface TileSnapshot {
  tiles: ReadonlyMap<string, TileData>;
  error: string | null;
}

/** A framework-free, bounded tile store. Owns fetch de-duplication, failure back-off, LRU-ish pruning,
 *  and change notification; the hook on top stays declarative. Keys are opaque composite strings
 *  (version + filter + tile) so a manifest or filter switch can never collide with or resurrect stale
 *  rows. Every change publishes a fresh {@link TileSnapshot}. */
export class TileCache {
  private snapshot: TileSnapshot = { tiles: new Map(), error: null };
  private readonly loading = new Set<string>();
  private readonly failedAt = new Map<string, number>();
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
  ensure(key: string, load: () => Promise<TileData>, keepActive: () => Set<string>): void {
    if (this.snapshot.tiles.has(key) || this.loading.has(key)) return;
    if (Date.now() - (this.failedAt.get(key) ?? -Infinity) < RETRY_MS) return;

    this.loading.add(key);
    load()
      .then((tile) => {
        this.loading.delete(key);
        this.failedAt.delete(key);
        const next = new Map(this.snapshot.tiles);
        next.set(key, tile);
        this.commit(prune(next, keepActive()), null);
      })
      .catch((e: unknown) => {
        this.loading.delete(key);
        this.failedAt.set(key, Date.now());
        this.commit(this.snapshot.tiles, e instanceof Error ? e.message : String(e));
        // Re-publish after the back-off so the driving effect re-runs and retries.
        setTimeout(() => this.commit(this.snapshot.tiles, this.snapshot.error), RETRY_MS);
      });
  }

  private commit(tiles: ReadonlyMap<string, TileData>, error: string | null): void {
    this.snapshot = { tiles, error };
    for (const l of this.listeners) l();
  }
}

/** Drop tiles outside `active`, keeping the heap bounded as the viewport moves. Evicted tiles re-fetch
 *  from the browser's HTTP cache (tiles are immutable), so a tighter cap trades a little re-decode on
 *  backtrack for a much smaller resident heap. */
function prune(tiles: Map<string, TileData>, active: Set<string>): Map<string, TileData> {
  if (tiles.size <= CAP) return tiles;
  for (const key of tiles.keys()) {
    if (tiles.size <= CAP) break;
    if (!active.has(key)) tiles.delete(key);
  }
  return tiles;
}
