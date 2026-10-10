import { describe, expect, it } from 'vitest';
import { netMeasure } from './netMeasure';
describe('netMeasure with a service worker in the path', () => {
  // The bug this pins: a SW-mediated response ALWAYS reports transferSize 0 — sw.js hands the page a
  // Response it constructed, so the network transfer it just performed is invisible. Reading
  // transferSize scored every miss as a free cache hit, and the dashboard showed 100% cached with
  // hundreds of MB moving on the wire. The worker's own verdict must win over Resource Timing.
  it('trusts the worker over transferSize on a miss', () => {
    const m = netMeasure('https://x/tiles/a/v1/1/2/3.arrow', 1_300_000, 'miss');
    expect(m.cached).toBe(false);
    expect(m.source).toBe('network');
    expect(m.wire).toBe(1_300_000); // the worker fetched them; body size is the honest stand-in
  });

  it('trusts the worker on a hit', () => {
    const m = netMeasure('https://x/tiles/a/v1/1/2/3.arrow', 1_300_000, 'hit');
    expect(m.cached).toBe(true);
    expect(m.source).toBe('sw');
    expect(m.wire).toBe(0);
  });

  it('never reads a miss as cached, whatever Resource Timing says', () => {
    // The exact false reading: a real fetch the SW performed, which the page sees as transferSize 0.
    expect(netMeasure('https://x/facts.pack?tile=3/6/3', 4_908_000, 'miss').cached).toBe(false);
  });

  it('falls back to Resource Timing when no worker answered', () => {
    // No tag ⇒ no SW in the path ⇒ transferSize is truthful again. No entry here, so: unknown, and
    // unknown must stay undefined rather than defaulting to "free".
    const m = netMeasure('https://x/never-fetched.arrow', 1000, null);
    expect(m.cached).toBeUndefined();
    expect(m.wire).toBeUndefined();
    expect(m.source).toBeUndefined();
  });
});
