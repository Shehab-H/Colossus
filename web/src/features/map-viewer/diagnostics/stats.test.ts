import { beforeEach, describe, expect, it } from 'vitest';
import type { PerfEvent } from '@/lib/perf/events';
import { setPerfEnabled, clearPerf, record, cumulative } from '@/lib/perf/recorder';
import { stats } from './stats';

const ev = (e: Partial<PerfEvent> & Pick<PerfEvent, 'stage'>): PerfEvent => ({ ms: 1, t: 0, ...e });

beforeEach(() => {
  setPerfEnabled(true);
  clearPerf();
});

describe('stats', () => {
  it('reports percentiles and summed cost per stage', () => {
    for (const ms of [1, 2, 3, 4, 5, 6, 7, 8, 9, 100]) record(ev({ stage: 'fold.client', ms }));
    const s = stats().find((x) => x.stage === 'fold.client')!;
    expect(s.n).toBe(10);
    expect(s.p50).toBe(6);
    expect(s.p95).toBe(100);
    expect(s.max).toBe(100);
    expect(s.total).toBe(145);
    expect(s.last).toBe(100);
  });

  it('separates fetched from cached requests', () => {
    record(ev({ stage: 'net.tile', bytes: 1000, wire: 1000, cached: false }));
    record(ev({ stage: 'net.tile', bytes: 2000, wire: 0, cached: true }));
    record(ev({ stage: 'net.tile', bytes: 4000 })); // unreadable — no Timing-Allow-Origin
    const s = stats().find((x) => x.stage === 'net.tile')!;
    expect(s.n).toBe(3);
    expect(s.cold).toBe(1);
    expect(s.warm).toBe(1);
    // Decoded counts everything the pipeline consumed; wire counts only what the network carried.
    expect(s.bytes).toBe(7000);
    expect(s.wire).toBe(1000);
  });

  it('leaves wire undefined when no request revealed its size', () => {
    record(ev({ stage: 'net.tile', bytes: 4000 }));
    expect(stats().find((x) => x.stage === 'net.tile')!.wire).toBeUndefined();
  });
});

describe('fold.server', () => {
  it('gets its own percentiles, separate from the round trip that carried it', () => {
    record(ev({ stage: 'fold.remote', ms: 100, bytes: 500, wire: 500, cached: false, serverMs: 80 }));
    record(ev({ stage: 'fold.server', ms: 80 }));
    record(ev({ stage: 'fold.remote', ms: 200, bytes: 500, wire: 500, cached: false, serverMs: 190 }));
    record(ev({ stage: 'fold.server', ms: 190 }));
    const server = stats().find((s) => s.stage === 'fold.server')!;
    expect(server.n).toBe(2);
    expect(server.p95).toBe(190);
    // The unwrapped stage must not double-count the round trip's bytes.
    expect(server.bytes).toBe(0);
    expect(cumulative().wire).toBe(1000);
  });
});
