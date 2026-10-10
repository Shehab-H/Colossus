export type PerfStage =
  | 'net.tile' // fetch a render tile (Arrow IPC)
  | 'net.facts' // fetch companion bytes — a pack block or coalesced slab plane runs (R1/R2/R5)
  | 'decode.ipc' // tableFromIPC over fetched bytes
  | 'decode.tile' // Arrow table → TileData (geometry, columns, filter slots)
  | 'decode.companion' // Arrow table / plane blocks → the fold's typed input
  | 'inflate' // DecompressionStream over a companion block
  | 'fold.client' // foldTile / foldSlab on this machine
  | 'fold.remote' // POST /fold round trip (carries the server's own compute ms)
  | 'fold.server' // the server's own DuckDB compute, unwrapped from fold.remote's X-Fold-Ms
  | 'layers'; // deck layer construction for the drawn cover

/** Which layer answered a fetch. The app has three caches with very different lifetimes and costs — the
 *  in-memory TileCache (384 MB, decoded), the browser's HTTP cache (disk, immutable tiles), and the
 *  service worker's Cache API (disk, production-only, what makes a returning session render offline).
 *  A single "cached" flag collapses the two disk caches into one number, which hides the one that
 *  actually matters in production. Memory-vs-disk within the HTTP cache is NOT distinguishable — no
 *  browser exposes it — so 'http' means "the HTTP cache, either tier". */
export type CacheSource = 'network' | 'http' | 'sw';

/** Response header sw.js stamps with its cache outcome ('hit' | 'miss'). The page has no other way to
 *  know: every SW-mediated response reports transferSize 0, so without this a miss that pulled megabytes
 *  reads identically to a free cache hit. Kept here beside the reader (netMeasure); sw.js is plain JS with
 *  no bundler and declares the same literal on its side. */
export const SW_CACHE_HEADER = 'X-Colossus-Cache';

export interface PerfEvent {
  stage: PerfStage;
  ms: number;
  /** performance.now() at completion — orders events across the worker boundary. */
  t: number;
  /** Usable bytes: decompressed and decoded. */
  bytes?: number;
  /** Bytes actually on the wire (transferSize). 0 on a cache hit — which is the true answer, not a
   *  missing one. Undefined only when genuinely unknowable (see netMeasure), so "unknown" never reads
   *  as "free" and a cache hit never reads as a download. */
  wire?: number;
  /** true = served from cache (nothing crossed the network), false = fetched, undefined = unknowable.
   *  Cold vs warm is the whole question for tiles and facts, so it is measured, not inferred. */
  cached?: boolean;
  /** WHICH cache answered — the app has two disk caches with very different lifetimes, and "cached" alone
   *  cannot tell them apart. See {@link CacheSource}. */
  source?: CacheSource;
  /** fold.remote only: the server's own compute ms (X-Fold-Ms). Round trip minus this is transport. */
  serverMs?: number;
  /** Rows, marks, or tiles — whatever the stage counts. */
  n?: number;
  /** Tile key or short label. */
  key?: string;
}

/** Running totals since load (or since `clear`). The event ring is a bounded WINDOW — once full, each new
 *  fetch evicts an old one, so every figure derived from it rises and falls as history ages out. That is
 *  the right behaviour for "what is happening now" (p50, goodput) and the wrong one for "what has this
 *  session cost", which only ever grows. These counters are accumulated at record time and never evicted,
 *  so they answer the second question exactly. */
export interface Cumulative {
  /** Bytes that actually crossed the network, every stage. */
  wire: number;
  /** Bytes the pipeline consumed that came from cache instead — what the network did NOT have to carry. */
  cache: number;
  /** The two disk caches, split. `cacheSw` is zero in dev by design (sw.js registers on prod builds only),
   *  so a non-zero value is itself the signal that the production cache is engaged. */
  cacheHttp: number;
  cacheSw: number;
  tileWire: number;
  tileCache: number;
  tileReqs: number;
  factsWire: number;
  factsCache: number;
  factsReqs: number;
  foldWire: number;
  foldReqs: number;
  /** Every event ever recorded, including the ones the window has since dropped. */
  events: number;
}

/** deck.gl's own once-per-second metrics. Structurally typed rather than imported from @deck.gl/core so
 *  this module stays renderer-agnostic (it also runs in the decode worker, which has no deck).
 *
 *  These are gauges, not durations — the current state of the GPU, resampled each second — so they are
 *  held as a latest-snapshot instead of going through the event ring, whose percentiles would be
 *  meaningless over a value that simply *is* what it is right now. `bufferMemory` is the direct readout
 *  of GPU residency: the bytes deck is actually holding on the card. */
export interface DeckSnapshot {
  fps: number;
  gpuTimePerFrame: number;
  cpuTimePerFrame: number;
  bufferMemory: number;
  textureMemory: number;
  gpuMemory: number;
  /** Time deck spent uploading attribute buffers — the layer-admission cost the `layers` stage misses,
   *  because that stage only times constructing the layer objects, not deck committing them to the GPU. */
  updateAttributesTime: number;
  framesRedrawn: number;
  layersCount: number;
  drawLayersCount: number;
  t: number;
}
