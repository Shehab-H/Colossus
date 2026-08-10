// Post-bake lifecycle instrumentation. Every stage a mark passes through between a baked tile on disk
// and a lit pixel emits here: network (tile + facts bytes), decode, fold (client or server), layer build,
// and frames. Off unless ?perf=1 — `record` early-returns, so an unflagged session pays a boolean test
// per stage and allocates nothing.
//
// This module is imported by BOTH the main thread and the decode worker. They are separate realms, so
// each gets its own instance: the worker fills its ring and `takeEvents()` drains it into the response
// message; the main thread's instance is the one the dashboard subscribes to (see tileLoader, which
// re-emits the worker's events after they cross the boundary with their timings intact).

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

const CAP = 4000;
// Frames live in their own ring: at 60/s they would evict every lifecycle event within a minute.
const FRAME_CAP = 240;
/** A frame this long is a visible hitch, not a dropped frame — the stutter the tile pipeline causes. */
export const LONG_FRAME_MS = 50;

let enabled = false;
let events: PerfEvent[] = [];
let frames: number[] = [];

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

const zeroCum = (): Cumulative => ({
  wire: 0,
  cache: 0,
  cacheHttp: 0,
  cacheSw: 0,
  tileWire: 0,
  tileCache: 0,
  tileReqs: 0,
  factsWire: 0,
  factsCache: 0,
  factsReqs: 0,
  foldWire: 0,
  foldReqs: 0,
  events: 0,
});

let cum: Cumulative = zeroCum();

export const cumulative = (): Cumulative => cum;

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

let deckSnap: DeckSnapshot | null = null;

export const deckSnapshot = (): DeckSnapshot | null => deckSnap;

export function recordDeck(m: Omit<DeckSnapshot, 't'>): void {
  if (!enabled) return;
  deckSnap = { ...m, t: performance.now() };
  scheduleNotify();
}

/** The in-memory tile cache's live gauge: decoded bytes resident against its budget, and how many tiles
 *  it has evicted. Evictions are the thrash signal — a pan that evicts and immediately re-fetches the
 *  same ground shows up here as a rising count with no new territory covered, and nowhere else. */
export interface CacheGauge {
  resident: number;
  budget: number;
  tiles: number;
  evictions: number;
}

/** Fold one event into the running totals. Cache hits contribute their decoded size to `cache` and
 *  nothing to `wire` — the two are counted in comparable units (a cached tile and a fetched tile are both
 *  measured as the bytes the pipeline received), which is what makes the comparison meaningful. */
function accumulate(e: PerfEvent): void {
  cum.events++;
  const wire = e.wire ?? 0;
  const cached = e.cached === true ? (e.bytes ?? 0) : 0;
  if (e.stage === 'net.tile') {
    cum.tileWire += wire;
    cum.tileCache += cached;
    cum.tileReqs++;
  } else if (e.stage === 'net.facts') {
    cum.factsWire += wire;
    cum.factsCache += cached;
    cum.factsReqs++;
  } else if (e.stage === 'fold.remote') {
    cum.foldWire += wire;
    cum.foldReqs++;
  } else return; // non-network stages carry no bytes
  cum.wire += wire;
  cum.cache += cached;
  if (e.source === 'sw') cum.cacheSw += cached;
  else if (e.source === 'http') cum.cacheHttp += cached;
}

/** `?perf=1` turns the dashboard and every probe on (mirrors foldRouteOverride's URL-flag idiom). */
export function perfEnabled(search?: string): boolean {
  const s = search ?? (typeof location === 'undefined' ? '' : location.search);
  return new URLSearchParams(s).get('perf') === '1';
}

export const setPerfEnabled = (v: boolean): void => {
  enabled = v;
};
export const isPerfOn = (): boolean => enabled;

export function record(e: PerfEvent): void {
  if (!enabled) return;
  accumulate(e);
  events.push(e);
  if (events.length > CAP) events.splice(0, events.length - CAP);
  scheduleNotify();
}

export function recordFrame(ms: number): void {
  if (!enabled) return;
  frames.push(ms);
  if (frames.length > FRAME_CAP) frames.shift();
  scheduleNotify();
}

/** Worker side: hand every event collected for one request to the response message, and reset. */
export function takeEvents(): PerfEvent[] {
  const out = events;
  events = [];
  return out;
}

/** Main side: re-emit events that were measured in the worker. Their `t` is on the worker's clock, which
 *  shares an origin with the main thread's (both are performance.timeOrigin-based), so ordering holds. */
export function emitAll(list: PerfEvent[]): void {
  if (!enabled || !list.length) return;
  for (const e of list) {
    accumulate(e);
    events.push(e);
  }
  if (events.length > CAP) events.splice(0, events.length - CAP);
  scheduleNotify();
}

export const perfEvents = (): readonly PerfEvent[] => events;
export const frameSamples = (): readonly number[] => frames;

/** Reset both the window and the running totals — "clear" means start a fresh measurement, so a total
 *  that survived it would silently mix the last experiment into the next one. */
export function clearPerf(): void {
  events = [];
  frames = [];
  cum = zeroCum();
  deckSnap = null; // deck republishes within a second; a stale snapshot would outlive the reset
  bump();
}

// The dashboard renders from a version counter, notified on a timer rather than per event. A pan storms
// hundreds of events a second; re-rendering the dashboard on each would make the tool the thing it is
// measuring. 250ms is live to the eye and costs ~4 renders/second.
const NOTIFY_MS = 250;
const listeners = new Set<() => void>();
let version = 0;
let timer: ReturnType<typeof setTimeout> | undefined;

function bump(): void {
  version++;
  for (const l of listeners) l();
}

function scheduleNotify(): void {
  if (timer !== undefined) return;
  timer = setTimeout(() => {
    timer = undefined;
    bump();
  }, NOTIFY_MS);
}

export const perfStore = {
  subscribe(cb: () => void): () => void {
    listeners.add(cb);
    return () => {
      listeners.delete(cb);
    };
  },
  getSnapshot: (): number => version,
};

/** Time an async stage and record it. Returns the value, so a probe wraps a call without restructuring it. */
export async function timed<T>(stage: PerfStage, fn: () => Promise<T>, fields?: (v: T) => Omit<PerfEvent, 'stage' | 'ms' | 't'>): Promise<T> {
  if (!enabled) return fn();
  const t0 = performance.now();
  const v = await fn();
  const t = performance.now();
  record({ stage, ms: t - t0, t, ...fields?.(v) });
  return v;
}

/** Synchronous counterpart of `timed`. */
export function timedSync<T>(stage: PerfStage, fn: () => T, fields?: (v: T) => Omit<PerfEvent, 'stage' | 'ms' | 't'>): T {
  if (!enabled) return fn();
  const t0 = performance.now();
  const v = fn();
  const t = performance.now();
  record({ stage, ms: t - t0, t, ...fields?.(v) });
  return v;
}

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

/** Resource Timing keeps a bounded entry buffer (250 by default). Once full it silently stops recording,
 *  and every later fetch reports "unknown" — a long pan would go blind halfway through. Raise it, and
 *  clear on overflow so measurement resumes rather than degrading without saying so. Called on both the
 *  main thread and the worker: each realm has its own timeline, and tiles are fetched in the worker. */
export function initResourceTiming(): void {
  try {
    performance.setResourceTimingBufferSize?.(5000);
    (performance as unknown as EventTarget).addEventListener?.('resourcetimingbufferfull', () =>
      performance.clearResourceTimings(),
    );
  } catch {
    // Not every realm exposes these; the probes degrade to "unknown", never to a wrong number.
  }
}

export interface StageStat {
  stage: PerfStage;
  n: number;
  last: number;
  p50: number;
  p95: number;
  max: number;
  /** Summed wall time — what the stage actually cost over the window, not per-call. */
  total: number;
  bytes: number;
  /** Wire bytes summed over the requests that actually fetched. Undefined when none of them knew. */
  wire?: number;
  /** Requests that hit the network. */
  cold: number;
  /** Requests served from cache — zero bytes, and the reason a warm pan is free. */
  warm: number;
}

const percentile = (sorted: number[], p: number): number =>
  sorted.length === 0 ? 0 : sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];

export function stats(list: readonly PerfEvent[] = events): StageStat[] {
  const by = new Map<PerfStage, PerfEvent[]>();
  for (const e of list) {
    const a = by.get(e.stage);
    if (a) a.push(e);
    else by.set(e.stage, [e]);
  }
  const out: StageStat[] = [];
  for (const [stage, evs] of by) {
    const ms = evs.map((e) => e.ms).sort((a, b) => a - b);
    const known = evs.filter((e) => e.wire !== undefined);
    out.push({
      stage,
      n: evs.length,
      last: evs[evs.length - 1].ms,
      p50: percentile(ms, 50),
      p95: percentile(ms, 95),
      max: ms[ms.length - 1] ?? 0,
      total: evs.reduce((s, e) => s + e.ms, 0),
      bytes: evs.reduce((s, e) => s + (e.bytes ?? 0), 0),
      wire: known.length ? known.reduce((s, e) => s + (e.wire ?? 0), 0) : undefined,
      cold: evs.filter((e) => e.cached === false).length,
      warm: evs.filter((e) => e.cached === true).length,
    });
  }
  return out.sort((a, b) => STAGE_ORDER.indexOf(a.stage) - STAGE_ORDER.indexOf(b.stage));
}

export const STAGE_ORDER: PerfStage[] = [
  'net.tile',
  'net.facts',
  'inflate',
  'decode.ipc',
  'decode.tile',
  'decode.companion',
  'fold.client',
  'fold.remote',
  'fold.server',
  'layers',
];

/** One network stage's cold/warm split. `wire` counts only what was actually fetched, so it is never
 *  inflated by cache hits; `decoded` counts everything the pipeline consumed, cached or not. */
export interface NetTotals {
  reqs: number;
  cold: number;
  warm: number;
  /** Requests whose size the browser wouldn't reveal (no Timing-Allow-Origin). */
  unknown: number;
  wire: number;
  decoded: number;
}

export interface PerfTotals {
  tiles: NetTotals;
  facts: NetTotals;
  foldBytes: number;
  /** Goodput over the requests that actually fetched, MB/s. Null when nothing cold was measured — a fully
   *  warm window has no throughput to report, and 0 would be a lie. */
  throughputMBs: number | null;
  frames: number;
  longFrames: number;
  fps: number | null;
  /** fold.remote only: mean server compute vs mean round trip. */
  foldServerMs: number | null;
  foldRoundTripMs: number | null;
}

const sumOf = (evs: readonly PerfEvent[], f: (e: PerfEvent) => number | undefined) =>
  evs.reduce((a, e) => a + (f(e) ?? 0), 0);

function netTotals(evs: readonly PerfEvent[]): NetTotals {
  return {
    reqs: evs.length,
    cold: evs.filter((e) => e.cached === false).length,
    warm: evs.filter((e) => e.cached === true).length,
    unknown: evs.filter((e) => e.cached === undefined).length,
    wire: sumOf(evs, (e) => e.wire),
    decoded: sumOf(evs, (e) => e.bytes),
  };
}

export function totals(list: readonly PerfEvent[] = events, fr: readonly number[] = frames): PerfTotals {
  const of = (s: PerfStage) => list.filter((e) => e.stage === s);
  const remote = of('fold.remote');
  const withServer = remote.filter((e) => e.serverMs !== undefined);
  const longFrames = fr.filter((f) => f >= LONG_FRAME_MS).length;
  const meanFrame = fr.length ? fr.reduce((a, b) => a + b, 0) / fr.length : 0;
  // Throughput counts ONLY requests that actually hit the network. Including cache hits — which return
  // megabytes in microseconds — produced a goodput figure that was pure fiction.
  const fetched = [...of('net.tile'), ...of('net.facts')].filter((e) => e.cached === false);
  const netMs = sumOf(fetched, (e) => e.ms);
  const netWire = sumOf(fetched, (e) => e.wire);
  return {
    tiles: netTotals(of('net.tile')),
    facts: netTotals(of('net.facts')),
    foldBytes: sumOf(remote, (e) => e.bytes),
    // Sum of bytes over sum of time: concurrent fetches overlap, so this is aggregate goodput across the
    // window, deliberately not the mean of per-request rates (which would over-report under parallelism).
    throughputMBs: netMs > 0 && netWire > 0 ? netWire / 1e6 / (netMs / 1000) : null,
    frames: fr.length,
    longFrames,
    fps: meanFrame > 0 ? 1000 / meanFrame : null,
    foldServerMs: withServer.length ? sumOf(withServer, (e) => e.serverMs) / withServer.length : null,
    foldRoundTripMs: remote.length ? sumOf(remote, (e) => e.ms) / remote.length : null,
  };
}

/** A rAF loop sampling frame-to-frame deltas — the deck layer-admission hitch shows up here as a long
 *  frame. Only runs while the dashboard is mounted (i.e. ?perf=1), because a permanent rAF loop keeps the
 *  compositor awake and would itself perturb idle-time work like the predictive prefetch. */
export function startFrameProbe(): () => void {
  let last = performance.now();
  let raf = requestAnimationFrame(function tick() {
    const now = performance.now();
    recordFrame(now - last);
    last = now;
    raf = requestAnimationFrame(tick);
  });
  return () => cancelAnimationFrame(raf);
}

/** The GPU actually rendering this session, via WEBGL_debug_renderer_info. Static, read once and cached —
 *  it costs a throwaway context, and the extension is absent in some privacy configurations (undefined,
 *  not a guess). Not a measurement, but without it none of the GPU or frame numbers above are comparable
 *  across machines: "8ms/frame" means nothing until you know what drew it. */
let gpuNameCache: string | null | undefined;

export function gpuName(): string | null {
  if (gpuNameCache !== undefined) return gpuNameCache;
  gpuNameCache = null;
  try {
    const c = document.createElement('canvas');
    const gl = c.getContext('webgl2') ?? c.getContext('webgl');
    const ext = gl?.getExtension('WEBGL_debug_renderer_info');
    if (gl && ext) {
      // Renderer strings are long and vendor-decorated, e.g.
      //   ANGLE (AMD, AMD Radeon RX 6750 XT (0x00007479) Direct3D11 vs_5_0 ps_5_0, D3D11)
      // The second comma-field is the device; the hex id and shader-model suffix are noise.
      const raw = String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) ?? '');
      const m = /\(([^,]+),\s*([^,]+)/.exec(raw);
      const name = (m ? m[2] : raw).replace(/\s*\(0x[0-9a-f]+\).*$/i, '').trim();
      gpuNameCache = name.slice(0, 30) || null;
    }
  } catch {
    gpuNameCache = null;
  }
  return gpuNameCache;
}

/** `window.__perf` — the programmatic read. Lets a browser-driving agent (or you, in the console) pull
 *  the same numbers the dashboard shows without scraping the DOM: `__perf.dump()`. */
export function installPerfGlobal(): void {
  if (typeof window === 'undefined') return;
  (window as unknown as Record<string, unknown>).__perf = {
    on: () => setPerfEnabled(true),
    off: () => setPerfEnabled(false),
    isOn: () => enabled,
    events: () => events.slice(),
    frames: () => frames.slice(),
    stats: () => stats(),
    totals: () => totals(),
    deck: () => deckSnap,
    gpu: () => gpuName(),
    clear: () => clearPerf(),
    dump: () => ({ stats: stats(), totals: totals(), deck: deckSnap, gpu: gpuName() }),
  };
}
