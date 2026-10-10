import type { PerfStage, PerfEvent } from './events';
import { isPerfOn, record } from './recorder';
/** Time an async stage and record it. Returns the value, so a probe wraps a call without restructuring it. */
export async function timed<T>(stage: PerfStage, fn: () => Promise<T>, fields?: (v: T) => Omit<PerfEvent, 'stage' | 'ms' | 't'>): Promise<T> {
  if (!isPerfOn()) return fn();
  const t0 = performance.now();
  const v = await fn();
  const t = performance.now();
  record({ stage, ms: t - t0, t, ...fields?.(v) });
  return v;
}

/** Synchronous counterpart of `timed`. */
export function timedSync<T>(stage: PerfStage, fn: () => T, fields?: (v: T) => Omit<PerfEvent, 'stage' | 'ms' | 't'>): T {
  if (!isPerfOn()) return fn();
  const t0 = performance.now();
  const v = fn();
  const t = performance.now();
  record({ stage, ms: t - t0, t, ...fields?.(v) });
  return v;
}
