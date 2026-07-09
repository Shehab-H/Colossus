import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import type { Manifest } from '../lib/manifest';
import { TileCache } from '../lib/tileCache';
import type { TileData } from '../lib/tileData';
import { tileLoader } from '../lib/tileLoader';
import { coverTiles, selectTiles, tileKey } from '../lib/tiling';
import { boundsFromViewport, viewportFor, type CameraState } from '../lib/viewport';

export interface RenderedTile {
  key: string;
  data: TileData;
}

// Composite key: version + filter + tile. A manifest or filter switch changes every key, so stale rows
// are never drawn (old entries fall out via pruning). Measure/range are NOT in the key — a tile carries
// every measure, so switching measure recolors from cache with no re-fetch.
const compositeKey = (version: string, filterSql: string, key: string) => `${version}|${filterSql}|${key}`;

/** Streams the viewport's tile set through a {@link TileCache}: selects visible tiles, fetches misses,
 *  and answers with the drawable cover (loaded stand-ins hold the screen until a quad's tiles all
 *  arrive, then the swap lands on identical pixels — see coverTiles). Re-renders are driven by the
 *  cache's immutable snapshot, so every dependency below is a value actually read — no trigger deps. */
export function useTiles(
  manifest: Manifest | null,
  camera: CameraState | null,
  size: { width: number; height: number },
  filterSql: string,
) {
  const cache = useRef(new TileCache()).current;
  const snapshot = useSyncExternalStore(cache.subscribe, cache.getSnapshot);
  const [selKeys, setSelKeys] = useState<string[]>([]);

  useEffect(() => {
    if (!manifest || !camera) return;
    const vp = viewportFor(manifest, camera, size);
    if (!vp) return;

    const keys = selectTiles(manifest, boundsFromViewport(vp));
    // Keep the previous array identity when the selection didn't change: the layers memo then skips
    // entirely during a pan/zoom within the same tile set instead of rebuilding every camera frame.
    setSelKeys((prev) => (prev.length === keys.length && prev.every((v, i) => v === keys[i]) ? prev : keys));

    const ck = (k: string) => compositeKey(manifest.version, filterSql, k);
    const keepActive = () => {
      const cover = coverTiles(keys, (k) => cache.has(ck(k)));
      return new Set([...keys, ...cover].map(ck));
    };
    for (const key of keys) {
      cache.ensure(ck(key), () => tileLoader.load(manifest.view, manifest.version, key, filterSql), keepActive);
    }
    // `snapshot` is a dep so a resolved/failed load re-runs selection (and any retry) against fresh data.
  }, [manifest, camera, size, filterSql, snapshot, cache]);

  // Draw the cover, not the raw selection, with each tile's loaded data attached.
  const rendered = useMemo<RenderedTile[]>(() => {
    if (!manifest) return [];
    const dataFor = (k: string) => snapshot.tiles.get(compositeKey(manifest.version, filterSql, k));
    return coverTiles(selKeys, (k) => !!dataFor(k))
      .map((key) => ({ key, data: dataFor(key) }))
      .filter((t): t is RenderedTile => !!t.data);
  }, [manifest, selKeys, filterSql, snapshot]);

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
    selKeys.every((k) => tileIndex.get(k)?.isLeaf && snapshot.tiles.has(compositeKey(manifest.version, filterSql, k)));

  return { selKeys, rendered, marksLoaded, atFullFidelity, loadError: snapshot.error };
}
