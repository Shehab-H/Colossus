// Thin client for the tile-cache service worker (public/sw.js). Registered in dev as well as production:
// the SW's fetch handler only claims versioned tile paths and ranged pack blocks and returns early for
// everything else (see sw.js), so Vite's HMR modules never enter its path — and running it in dev is what
// makes the tile disk cache observable (?perf=1 reports it as the `sw` source) instead of a production-only
// black box. Tile URLs carry their bake version, so a re-bake mints new cache entries and the SW's version
// rotation drops the old ones; a stale tile can't outlive its version.
//
// `?nosw=1` skips registration and unregisters any existing worker — the way to measure a genuinely cold
// load once the disk cache is warm, since a cache-first SW would otherwise answer every tile forever.

/** Register the tile-cache service worker. `?nosw=1` opts out and tears down an existing registration. */
export function registerTileCache(): void {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
  if (new URLSearchParams(window.location.search).get('nosw') === '1') {
    void unregisterTileCache();
    return;
  }
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      /* SW registration is best-effort; the app fetches straight through if it fails */
    });
  });
}

/** Drop the tile caches and the worker itself — a cold-load reset for measurement. Resolves once the
 *  caches are gone; the page still needs a reload to fetch uncontrolled. */
export async function unregisterTileCache(): Promise<void> {
  if (typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
  try {
    const regs = await navigator.serviceWorker.getRegistrations();
    await Promise.all(regs.map((r) => r.unregister()));
    const keys = await caches.keys();
    await Promise.all(keys.filter((k) => k.startsWith('tiles-')).map((k) => caches.delete(k)));
  } catch {
    /* best-effort: a browser that denies cache access simply stays warm */
  }
}

/** Tell the SW which (view, version) is now active, so it can drop the view's stale-version caches
 *  (version rotation is the cache GC). No-op until a controller is active. */
export function activateTileVersion(viewId: string, version: string): void {
  if (typeof navigator === 'undefined') return;
  navigator.serviceWorker?.controller?.postMessage({ viewId, version });
}
