// Colossus tile cache — serves immutable, versioned tiles cache-first from the Cache API, so reloads and
// returning sessions render without the network even after the HTTP cache evicts. Only versioned tile
// paths (`.../<viewId>/<version>/….arrow`) and companion pack blocks (`…/facts.pack?tile=z/x/y&r=<off>-<len>`,
// ranged) are cached; latest.json, manifests, and the API pass through.
// Version rotation is the GC: on manifest activation the client posts {viewId, version}, and caches for
// the view's other versions are dropped. Registered for production builds only (see swClient.ts). Plain
// JS, no bundler, no dependencies — keep it small.

const TILE_RE = /\/[^/]+\/v[^/]+\/\d+\/\d+\/\d+(\.facts)?\.arrow$/; // <viewId>/<version>/z/x/y[.facts].arrow
const PACK_RE = /\/([^/]+)\/(v[^/]+)\/[^/]+\.pack$/; // <viewId>/<version>/facts.pack (+ ?tile= and a Range header)

// Where a response actually came from. The page CANNOT work this out for itself: a response this worker
// constructs — or replays out of the Cache API — reports transferSize 0 to Resource Timing either way,
// so a miss that just pulled megabytes over the wire is indistinguishable from a free cache hit. This
// worker is the only party that knows, so it says so on every response it answers.
const CACHE_HEADER = 'X-Colossus-Cache';

/** Re-emit a response carrying the cache outcome. `res.body` streams through rather than being buffered,
 *  so this adds a header, not a copy — the tile hot path is untouched. */
function tagged(res, outcome) {
  const headers = new Headers(res.headers);
  headers.set(CACHE_HEADER, outcome);
  return new Response(res.body, { status: res.status, statusText: res.statusText, headers });
}

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
  if (e.request.method !== 'GET') return;
  // A companion plane run ranged out of the pack archive. The cache key is the full URL: `?tile=z/x/y`
  // scopes it to the tile and `&r=<off>-<len>` (see fetchSlabPlanes) to the exact byte range, so each
  // plane run — the whole tile, one measure's planes, or a delta on a measure switch — is its own entry.
  const pm = url.pathname.match(PACK_RE);
  if (pm && url.searchParams.has('tile') && e.request.headers.has('range')) {
    e.respondWith(cachedBlock(cacheName(pm[1], pm[2]), e.request));
    return;
  }
  const m = url.pathname.match(/\/([^/]+)\/(v[^/]+)\/\d+\/\d+\/\d+(?:\.facts)?\.arrow$/);
  if (!TILE_RE.test(url.pathname) || !m) return;
  e.respondWith(cacheFirst(cacheName(m[1], m[2]), e.request));
});

function cacheName(viewId, version) {
  return `tiles-${viewId}-${version}`;
}

async function cacheFirst(name, request) {
  const cache = await caches.open(name);
  const hit = await cache.match(request);
  if (hit) return tagged(hit, 'hit');
  const res = await fetch(request);
  if (res.ok) await putSafe(cache, request, res.clone());
  return tagged(res, 'miss');
}

// One companion block. The Cache API rejects 206 responses, so the fetched block is re-wrapped as a
// 200 whose body is exactly the block's bytes. Only an exact-length body is cached — a server that
// ignored Range returns the whole archive, and storing that once per tile would drain the quota; it
// passes through instead (the client slices it).
async function cachedBlock(name, request) {
  const cache = await caches.open(name);
  const hit = await cache.match(request);
  if (hit) return tagged(hit, 'hit');
  const res = await fetch(request);
  if (!res.ok) return res;
  const range = /bytes=(\d+)-(\d+)/.exec(request.headers.get('range'));
  const buf = await res.arrayBuffer();
  if (!range || buf.byteLength !== Number(range[2]) - Number(range[1]) + 1) {
    const headers = new Headers(res.headers);
    headers.set(CACHE_HEADER, 'miss');
    return new Response(buf, { status: res.status, statusText: res.statusText, headers });
  }
  // Stored WITHOUT a verdict of its own: the header is stamped per answer (tagged() on the hit path
  // overwrites it), so a cached block can never replay this miss's verdict to a later reader.
  const block = new Response(buf, { status: 200, headers: { 'Content-Type': 'application/octet-stream' } });
  await putSafe(cache, request, block.clone());
  return tagged(block, 'miss');
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
