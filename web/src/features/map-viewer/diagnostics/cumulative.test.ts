import { beforeEach, describe, expect, it } from 'vitest';
import type { PerfEvent } from '@/lib/perf/events';
import { setPerfEnabled, clearPerf, record, perfEvents, cumulative } from '@/lib/perf/recorder';
import { totals } from './totals';

const ev = (e: Partial<PerfEvent> & Pick<PerfEvent, 'stage'>): PerfEvent => ({ ms: 1, t: 0, ...e });

beforeEach(() => {
  setPerfEnabled(true);
  clearPerf();
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
