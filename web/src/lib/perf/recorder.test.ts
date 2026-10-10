import { beforeEach, describe, expect, it } from 'vitest';
import type { PerfEvent } from './events';
import { setPerfEnabled, clearPerf, perfEnabled, record, recordFrame, perfEvents, isPerfOn, cumulative } from './recorder';
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

describe('cache source', () => {
  it('splits the two disk caches, which "cached" alone collapses', () => {
    // sw.js and the browser HTTP cache have very different lifetimes: the SW copy survives an HTTP-cache
    // eviction and is what makes a returning session render offline. One "cached" total hides it.
    record(ev({ stage: 'net.tile', bytes: 1000, wire: 0, cached: true, source: 'http' }));
    record(ev({ stage: 'net.tile', bytes: 3000, wire: 0, cached: true, source: 'sw' }));
    record(ev({ stage: 'net.facts', bytes: 500, wire: 0, cached: true, source: 'sw' }));
    record(ev({ stage: 'net.tile', bytes: 200, wire: 200, cached: false, source: 'network' }));
    const c = cumulative();
    expect(c.cacheHttp).toBe(1000);
    expect(c.cacheSw).toBe(3500);
    expect(c.cache).toBe(4500); // both disk caches
    expect(c.wire).toBe(200); // network is not a cache
  });

  it('counts a source-less cache hit toward the total but neither disk cache', () => {
    // An older entry, or a realm that reports no workerStart: still truthfully a cache hit, just not
    // attributable. Guessing a cache for it would invent a number.
    record(ev({ stage: 'net.tile', bytes: 800, wire: 0, cached: true }));
    const c = cumulative();
    expect(c.cache).toBe(800);
    expect(c.cacheHttp).toBe(0);
    expect(c.cacheSw).toBe(0);
  });
});
