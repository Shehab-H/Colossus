import type { PerfEvent, Cumulative, DeckSnapshot } from './events';
// Post-bake lifecycle instrumentation. Every stage a mark passes through between a baked tile on disk
// and a lit pixel emits here: network (tile + facts bytes), decode, fold (client or server), layer build,
// and frames. Off unless ?perf=1 — `record` early-returns, so an unflagged session pays a boolean test
// per stage and allocates nothing.
//
// This module is imported by BOTH the main thread and the decode worker. They are separate realms, so
// each gets its own instance: the worker fills its ring and `takeEvents()` drains it into the response
// message; the main thread's instance is the one the dashboard subscribes to (see tileLoader, which
// re-emits the worker's events after they cross the boundary with their timings intact).

const CAP = 4000;
// Frames live in their own ring: at 60/s they would evict every lifecycle event within a minute.
const FRAME_CAP = 240;

let enabled = false;
let events: PerfEvent[] = [];
let frames: number[] = [];

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

let deckSnap: DeckSnapshot | null = null;

export const deckSnapshot = (): DeckSnapshot | null => deckSnap;

export function recordDeck(m: Omit<DeckSnapshot, 't'>): void {
  if (!enabled) return;
  deckSnap = { ...m, t: performance.now() };
  scheduleNotify();
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
