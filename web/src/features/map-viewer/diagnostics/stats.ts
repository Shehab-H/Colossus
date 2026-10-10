import type { PerfStage, PerfEvent } from '@/lib/perf/events';
import { perfEvents } from '@/lib/perf/recorder';

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

export function stats(list: readonly PerfEvent[] = perfEvents()): StageStat[] {
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
