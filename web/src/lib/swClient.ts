// Thin client for the tile-cache service worker (public/sw.js). Production only — a service worker under
// Vite's dev HMR is misery, so dev never registers one and the app fetches straight through.

/** Register the tile-cache service worker on production builds. No-op in dev or where SW is unsupported. */
export function registerTileCache(): void {
  if (!import.meta.env.PROD || typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      /* SW registration is best-effort; the app fetches straight through if it fails */
    });
  });
}

/** Tell the SW which (view, version) is now active, so it can drop the view's stale-version caches
 *  (version rotation is the cache GC). No-op until a controller is active. */
export function activateTileVersion(viewId: string, version: string): void {
  if (typeof navigator === 'undefined') return;
  navigator.serviceWorker?.controller?.postMessage({ viewId, version });
}
