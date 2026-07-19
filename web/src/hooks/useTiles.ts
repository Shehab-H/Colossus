import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import type { Manifest } from '../lib/manifest';
import { renderFetch } from '../lib/tileData';
import { renderDecodeView } from '../lib/channels';
import type { FilterSlots } from '../lib/gpuFilter';
import { TileCache, TILE_BUDGET_BYTES } from '../lib/tileCache';
import type { CacheGauge } from '../lib/perf';
import type { TileData } from '../lib/tileData';
import { tileLoader } from '../lib/tileLoader';
import { coverTiles, prefetchCandidates, selectTiles, tileKey } from '../lib/tiling';
import { boundsFromViewport, viewportFor, type CameraState } from '../lib/viewport';

// Prefetch never competes with demand loads: it only runs once the cache is below this share of budget.
const PREFETCH_BUDGET_FRACTION = 0.75;

const requestIdle = (fn: () => void): number =>
  typeof requestIdleCallback === 'function' ? requestIdleCallback(fn, { timeout: 500 }) : window.setTimeout(fn, 200);
const cancelIdle = (id: number): void => {
  if (typeof cancelIdleCallback === 'function') cancelIdleCallback(id);
  else clearTimeout(id);
};

export interface RenderedTile {
  key: string;
  data: TileData;
}

// Composite key: version + tile. Filter, measure, and scale are NOT in the key — a tile carries every
// measure and its GPU filter slots, so switching any of them updates uniforms/attributes with no
// re-fetch. Only a manifest (version) or tile change is a new cache entry.
const compositeKey = (version: string, key: string) => `${version}|${key}`;

/** Streams the viewport's tile set through a {@link TileCache}: selects visible tiles, fetches misses,
 *  and answers with the drawable cover (loaded stand-ins hold the screen until a quad's tiles all
 *  arrive, then the swap lands on identical pixels — see coverTiles). Re-renders are driven by the
 *  cache's immutable snapshot, so every dependency below is a value actually read — no trigger deps.
 *  `slots` rides into the worker so each tile bakes its GPU filter attribute once; it is not part of the
 *  cache identity (the filter is a uniform, not a reason to re-fetch). */
export function useTiles(
  manifest: Manifest | null,
  camera: CameraState | null,
  size: { width: number; height: number },
  slots: FilterSlots | null,
) {
  const cache = useRef(new TileCache()).current;
  const snapshot = useSyncExternalStore(cache.subscribe, cache.getSnapshot);
  const [selKeys, setSelKeys] = useState<string[]>([]);

  // The tile decoder sees the effective render channels in the group regime (measures + id), not the
  // authored perFact channels. Stable per manifest, so it is not a fetch trigger.
  const decodeView = useMemo(() => (manifest ? renderDecodeView(manifest) : null), [manifest]);

  useEffect(() => {
    if (!manifest || !camera) return;
    const vp = viewportFor(manifest, camera, size);
    if (!vp) return;

    const keys = selectTiles(manifest, boundsFromViewport(vp));
    // Keep the previous array identity when the selection didn't change: the layers memo then skips
    // entirely during a pan/zoom within the same tile set instead of rebuilding every camera frame.
    setSelKeys((prev) => (prev.length === keys.length && prev.every((v, i) => v === keys[i]) ? prev : keys));

    const ck = (k: string) => compositeKey(manifest.version, k);
    const keepActive = () => {
      const cover = coverTiles(keys, (k) => cache.has(ck(k)));
      return new Set([...keys, ...cover].map(ck));
    };
    // Loads only ever exist for selected keys (plus in-flight prefetches for this selection), so anything
    // in flight outside that set is a leftover from a zoom/pan the camera already left — cancel it before
    // requesting the new set. Keeping the prefetch candidates in the survive-set stops a resolving
    // prefetch's snapshot commit from cancelling its siblings.
    cache.abortStale(new Set([...keys, ...prefetchCandidates(manifest, keys)].map(ck)));
    const tileFormat = manifest.tileFormat ?? 1;
    for (const key of keys) {
      cache.ensure(
        ck(key),
        () => tileLoader.load(decodeView!, manifest.version, key, slots, tileFormat,
          renderFetch(manifest.renderPack, decodeView!.id, manifest.version, key)),
        keepActive,
      );
    }
    // `snapshot` is a dep so a resolved/failed load re-runs selection (and any retry) against fresh data.
  }, [manifest, decodeView, camera, size, slots, snapshot, cache]);

  // Predictive prefetch (fetch-locality §4.2): once the selection has been stable ~300ms and every
  // selected tile is resident, warm the likely-next tiles during idle — parents, the pan ring, children.
  // Guarded so it never competes with demand loads or evicts them (budget backstop), and cancelled by the
  // selection effect's abortStale as soon as the camera moves (the candidates leave the survive-set).
  useEffect(() => {
    if (!manifest || !decodeView || selKeys.length === 0) return;
    const ck = (k: string) => compositeKey(manifest.version, k);
    if (!selKeys.every((k) => cache.has(ck(k)))) return; // never race a demand load
    if (cache.bytesResident() > PREFETCH_BUDGET_FRACTION * TILE_BUDGET_BYTES) return; // don't evict for a guess
    const candidates = prefetchCandidates(manifest, selKeys);
    if (candidates.length === 0) return;

    const tileFormat = manifest.tileFormat ?? 1;
    const keepActive = () => new Set([...selKeys, ...candidates].map(ck));
    const run = () => {
      for (const key of candidates)
        cache.ensure(
          ck(key),
          () => tileLoader.load(decodeView, manifest.version, key, slots, tileFormat,
            renderFetch(manifest.renderPack, decodeView.id, manifest.version, key)),
          keepActive,
        );
    };
    // This effect re-runs on every load (snapshot dep), so the timer keeps resetting until the demand set
    // has settled — the ~300ms "stable selection" gate.
    let idleId: number | undefined;
    const timer = window.setTimeout(() => {
      idleId = requestIdle(run);
    }, 300);
    return () => {
      clearTimeout(timer);
      if (idleId !== undefined) cancelIdle(idleId);
    };
  }, [manifest, decodeView, selKeys, slots, snapshot, cache]);

  // Draw the cover, not the raw selection, with each tile's loaded data attached.
  const rendered = useMemo<RenderedTile[]>(() => {
    if (!manifest) return [];
    const dataFor = (k: string) => snapshot.tiles.get(compositeKey(manifest.version, k));
    return coverTiles(selKeys, (k) => !!dataFor(k))
      .map((key) => ({ key, data: dataFor(key) }))
      .filter((t): t is RenderedTile => !!t.data);
  }, [manifest, selKeys, snapshot]);

  const marksLoaded = rendered.reduce((s, t) => s + t.data.count, 0);

  // Are all on-screen tiles leaves (every real cell), or are some coarse aggregates? That's the line
  // between full fidelity and a "zoom in to resolve" preview (RULES R2).
  const tileIndex = useMemo(
    () => new Map((manifest?.tiles ?? []).map((t) => [tileKey(t.z, t.x, t.y), t])),
    [manifest],
  );
  const atFullFidelity =
    !!manifest &&
    rendered.length > 0 &&
    selKeys.every((k) => tileIndex.get(k)?.isLeaf && snapshot.tiles.has(compositeKey(manifest.version, k)));

  // Live residency gauge for the perf dashboard. Derived from `snapshot`, so it is recomputed exactly
  // when the cache actually changes — never on a timer, and never a reason to re-render on its own.
  const cacheGauge = useMemo<CacheGauge>(
    () => ({
      resident: cache.bytesResident(),
      budget: TILE_BUDGET_BYTES,
      tiles: snapshot.tiles.size,
      evictions: cache.evictions,
    }),
    [cache, snapshot],
  );

  return { selKeys, rendered, marksLoaded, atFullFidelity, loadError: snapshot.error, cacheGauge };
}
