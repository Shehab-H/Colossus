import { unregisterTileCache } from '@infrastructure/cache/tileCacheServiceWorker';

/** Reset persistent viewer caches, then reload into a genuinely cold renderer session. */
export async function reloadViewerCold(): Promise<void> {
  await unregisterTileCache();
  window.location.reload();
}
