import { beforeEach, describe, expect, it } from 'vitest';
import type { PerfEvent } from '@/lib/perf/events';
import { setPerfEnabled, clearPerf, record, recordFrame } from '@/lib/perf/recorder';
import { totals } from './totals';

const ev = (e: Partial<PerfEvent> & Pick<PerfEvent, 'stage'>): PerfEvent => ({ ms: 1, t: 0, ...e });

beforeEach(() => {
  setPerfEnabled(true);
  clearPerf();
});

describe('totals', () => {
  it('excludes cache hits from throughput', () => {
    // 1 MB really fetched over 100ms = 10 MB/s. The cached 50 MB returned in 1ms must not touch this —
    // counting it produced a goodput figure of pure fiction.
    record(ev({ stage: 'net.tile', ms: 100, bytes: 1e6, wire: 1e6, cached: false }));
    record(ev({ stage: 'net.tile', ms: 1, bytes: 50e6, wire: 0, cached: true }));
    expect(totals().throughputMBs).toBeCloseTo(10, 6);
  });

  it('reports no throughput for a fully warm window rather than zero', () => {
    record(ev({ stage: 'net.tile', ms: 1, bytes: 5e6, wire: 0, cached: true }));
    expect(totals().throughputMBs).toBeNull();
  });

  it('splits tiles and facts, cold/warm/unknown', () => {
    record(ev({ stage: 'net.tile', bytes: 100, wire: 100, cached: false }));
    record(ev({ stage: 'net.tile', bytes: 100, wire: 0, cached: true }));
    record(ev({ stage: 'net.facts', bytes: 30, wire: 30, cached: false }));
    record(ev({ stage: 'net.facts', bytes: 30 }));
    const t = totals();
    expect(t.tiles).toMatchObject({ reqs: 2, cold: 1, warm: 1, unknown: 0, wire: 100, decoded: 200 });
    expect(t.facts).toMatchObject({ reqs: 2, cold: 1, warm: 0, unknown: 1, wire: 30, decoded: 60 });
  });

  it('averages the remote fold round trip against the server-reported compute', () => {
    record(ev({ stage: 'fold.remote', ms: 100, bytes: 500, serverMs: 80 }));
    record(ev({ stage: 'fold.remote', ms: 200, bytes: 500, serverMs: 120 }));
    const t = totals();
    expect(t.foldRoundTripMs).toBe(150);
    expect(t.foldServerMs).toBe(100);
    expect(t.foldBytes).toBe(1000);
  });

  it('leaves the server split null when the header never arrived', () => {
    record(ev({ stage: 'fold.remote', ms: 100, bytes: 500 }));
    expect(totals().foldServerMs).toBeNull();
    expect(totals().foldRoundTripMs).toBe(100);
  });

  it('counts long frames and derives fps from the mean', () => {
    for (const f of [10, 10, 10, 10]) recordFrame(f);
    recordFrame(80);
    const t = totals();
    expect(t.frames).toBe(5);
    expect(t.longFrames).toBe(1); // only the 80ms one crosses LONG_FRAME_MS
    expect(t.fps).toBeCloseTo(1000 / 24, 6); // mean of [10,10,10,10,80] = 24ms
  });

  it('has no fps before any frame is sampled', () => {
    expect(totals().fps).toBeNull();
  });
});
