import { beforeEach, describe, expect, it } from 'vitest';
import {
  clearPerf,
  cumulative,
  isPerfOn,
  perfEnabled,
  perfEvents,
  record,
  recordFrame,
  setPerfEnabled,
  stats,
  totals,
  type PerfEvent,
} from './perf';

// The dashboard is only as trustworthy as this arithmetic: if p95 or the cold/warm split is wrong, every
// number the tool reports is wrong, and it would be wrong *confidently*. These pin the parts that a
// browser check can't isolate — cache hits vs real fetches, and what counts toward throughput.

const ev = (e: Partial<PerfEvent> & Pick<PerfEvent, 'stage'>): PerfEvent => ({ ms: 1, t: 0, ...e });

beforeEach(() => {
  setPerfEnabled(true);
  clearPerf();
});

describe('perfEnabled', () => {
  it('arms only on ?perf=1', () => {
    expect(perfEnabled('?perf=1')).toBe(true);
    expect(perfEnabled('?view=x&perf=1&fold=remote')).toBe(true);
    expect(perfEnabled('?perf=0')).toBe(false);
    expect(perfEnabled('?perf')).toBe(false);
    expect(perfEnabled('')).toBe(false);
  });
});

describe('record', () => {
  it('drops everything while disabled — an unflagged session collects nothing', () => {
    setPerfEnabled(false);
    record(ev({ stage: 'net.tile' }));
    recordFrame(16);
    expect(perfEvents()).toHaveLength(0);
    expect(isPerfOn()).toBe(false);
  });

  it('bounds the ring so a long session cannot grow without limit', () => {
    for (let i = 0; i < 4200; i++) record(ev({ stage: 'layers', ms: i }));
    expect(perfEvents()).toHaveLength(4000);
    // Oldest dropped, newest kept.
    expect(perfEvents()[perfEvents().length - 1].ms).toBe(4199);
  });
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

describe('cumulative', () => {
  it('keeps growing after the window has evicted the events it counted', () => {
    // The whole reason these exist: the ring is bounded, so window-derived byte figures fall as history
    // ages out. A session total that did the same would be unusable.
    for (let i = 0; i < 4100; i++) record(ev({ stage: 'net.tile', bytes: 1000, wire: 1000, cached: false }));
    expect(perfEvents()).toHaveLength(4000); // window dropped 100
    const c = cumulative();
    expect(c.tileReqs).toBe(4100); // total kept all of them
    expect(c.tileWire).toBe(4_100_000);
    expect(c.wire).toBe(4_100_000);
    // And the window genuinely under-reports the session, which is the point of showing both.
    expect(totals().tiles.wire).toBe(4_000_000);
  });

  it('separates bytes fetched from bytes served by cache, per kind', () => {
    record(ev({ stage: 'net.tile', bytes: 1000, wire: 1000, cached: false }));
    record(ev({ stage: 'net.tile', bytes: 3000, wire: 0, cached: true }));
    record(ev({ stage: 'net.facts', bytes: 200, wire: 200, cached: false }));
    record(ev({ stage: 'net.facts', bytes: 600, wire: 0, cached: true }));
    record(ev({ stage: 'fold.remote', bytes: 50, wire: 50, cached: false }));
    const c = cumulative();
    expect(c.tileWire).toBe(1000);
    expect(c.tileCache).toBe(3000);
    expect(c.factsWire).toBe(200);
    expect(c.factsCache).toBe(600);
    expect(c.foldWire).toBe(50);
    expect(c.wire).toBe(1250); // tiles + facts + fold, network only
    expect(c.cache).toBe(3600); // tiles + facts, cache only
  });

  it('ignores stages that move no bytes', () => {
    record(ev({ stage: 'fold.client', ms: 5 }));
    record(ev({ stage: 'layers', ms: 1 }));
    record(ev({ stage: 'decode.tile', ms: 2, bytes: 9999 }));
    const c = cumulative();
    expect(c.wire).toBe(0);
    expect(c.cache).toBe(0);
    expect(c.events).toBe(3); // still counted as events
  });

  it('resets with clear so one experiment cannot leak into the next', () => {
    record(ev({ stage: 'net.tile', bytes: 1000, wire: 1000, cached: false }));
    expect(cumulative().wire).toBe(1000);
    clearPerf();
    expect(cumulative().wire).toBe(0);
    expect(cumulative().events).toBe(0);
  });

  it('records nothing while disabled', () => {
    setPerfEnabled(false);
    record(ev({ stage: 'net.tile', bytes: 1000, wire: 1000, cached: false }));
    expect(cumulative().events).toBe(0);
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
