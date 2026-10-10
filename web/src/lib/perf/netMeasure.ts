import type { CacheSource } from './events';
/** What a just-completed fetch actually cost the network, and which layer answered it, from Resource
 *  Timing.
 *
 *  Outcomes, deliberately distinguished — collapsing them is how a perf tool starts lying:
 *   - transferSize > 0            → fetched; that many bytes crossed the wire.
 *   - transferSize 0, body > 0    → a real entry that moved no bytes: a cache hit. wire is 0, truthfully.
 *   - everything 0                → the sizes are hidden, not zero. Cross-origin without
 *                                   Timing-Allow-Origin zeroes ALL of them, so a genuine cache hit and an
 *                                   unreadable response look identical on transferSize alone;
 *                                   decodedBodySize is what tells them apart. Reports undefined.
 *
 *  `workerStart > 0` means the service worker handled the request, which separates the two disk caches:
 *  a SW hit served from the Cache API has workerStart set and transferSize 0, while a SW pass-through to
 *  the network keeps its real transferSize. Without this, sw.js's cache — the only one that survives an
 *  HTTP-cache eviction, and the entire reason a returning session renders offline — is invisible. */
export function netMeasure(
  url: string,
  bodyBytes: number,
  swOutcome?: string | null,
): { wire?: number; cached?: boolean; source?: CacheSource } {
  // The service worker is AUTHORITATIVE whenever it answered, and Resource Timing must not be consulted:
  // a response sw.js constructs or replays reports transferSize 0 whether the bytes came from the Cache
  // API or off the wire a millisecond earlier. Trusting transferSize here scored every SW miss as a free
  // cache hit — the whole app read as 100% cached while the network was saturated. sw.js tags the outcome
  // (X-Colossus-Cache) because it is the only party that can know it.
  if (swOutcome === 'hit') return { wire: 0, cached: true, source: 'sw' };
  // A SW miss: the worker fetched these bytes. Its own transfer is invisible to us, so the body size is
  // the honest stand-in — exact for tiles and pack blocks, which are served raw (already-compressed
  // bytes, no transport encoding), as the cold measurements confirm (wire == body).
  if (swOutcome === 'miss') return { wire: bodyBytes, cached: false, source: 'network' };

  try {
    const list = performance.getEntriesByName(url);
    const e = list[list.length - 1] as PerformanceResourceTiming | undefined;
    if (!e) return {};
    if (e.transferSize > 0) return { wire: e.transferSize, cached: false, source: 'network' };
    // No service worker in the path (it would have tagged the response), so transferSize 0 against a real
    // body is a genuine HTTP-cache hit.
    if (e.decodedBodySize > 0 || e.encodedBodySize > 0) return { wire: 0, cached: true, source: 'http' };
    return {};
  } catch {
    return {};
  }
}
