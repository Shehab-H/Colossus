// Colossus tile cache — serves immutable, versioned tiles cache-first from the Cache API, so reloads and
// returning sessions render without the network even after the HTTP cache evicts. Only versioned tile
// paths (`.../<viewId>/<version>/….arrow`) are cached; latest.json, manifests, and the API pass through.
// Version rotation is the GC: on manifest activation the client posts {viewId, version}, and caches for
// the view's other versions are dropped. Registered for production builds only (see swClient.ts). Plain
// JS, no bundler, no dependencies — keep it small.

const TILE_RE = /\/[^/]+\/v[^/]+\/\d+\/\d+\/\d+(\.facts)?\.arrow$/; // <viewId>/<version>/z/x/y[.facts].arrow

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));

self.addEventListener('message', (e) => {
  const { viewId, version } = e.data || {};
  if (!viewId || !version) return;
  e.waitUntil(
    caches.keys().then((keys) => {
      const keep = cacheName(viewId, version);
      return Promise.all(
        keys.filter((k) => k.startsWith(`tiles-${viewId}-`) && k !== keep).map((k) => caches.delete(k)),
      );
    }),
  );
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || !TILE_RE.test(url.pathname)) return;
  const m = url.pathname.match(/\/([^/]+)\/(v[^/]+)\/\d+\/\d+\/\d+(?:\.facts)?\.arrow$/);
  if (!m) return;
  e.respondWith(cacheFirst(cacheName(m[1], m[2]), e.request));
});

function cacheName(viewId, version) {
  return `tiles-${viewId}-${version}`;
}

async function cacheFirst(name, request) {
  const cache = await caches.open(name);
  const hit = await cache.match(request);
  if (hit) return hit;
  const res = await fetch(request);
  if (res.ok) await putSafe(cache, request, res.clone());
  return res;
}

// Quota-safe put: on QuotaExceededError, evict the oldest tile cache entirely and retry once; give up to
// network-only after that. A quota error must never surface as a tile load failure.
async function putSafe(cache, request, response) {
  try {
    await cache.put(request, response);
  } catch (err) {
    if (!err || err.name !== 'QuotaExceededError') return;
    const keys = await caches.keys();
    const victim = keys.find((k) => k.startsWith('tiles-'));
    if (!victim) return;
    await caches.delete(victim);
    try {
      await cache.put(request, response);
    } catch {
      /* second failure → serve network-only */
    }
  }
}
