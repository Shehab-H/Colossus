import type { PerfEvent, PerfStage } from '@/lib/perf/events';
import { perfEvents, frameSamples } from '@/lib/perf/recorder';
import { LONG_FRAME_MS } from './frameProbe';

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

export function totals(list: readonly PerfEvent[] = perfEvents(), fr: readonly number[] = frameSamples()): PerfTotals {
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
