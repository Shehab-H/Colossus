import { describe, expect, it } from 'vitest';
import { TileCache, type CancellableLoad } from './tileCache';
import type { TileData } from './tileData';

// A fake tile whose only job is to weigh `mb` megabytes to the cache's byte budget.
const tile = (mb: number): TileData =>
  ({ count: 0, values: {}, positions: { byteLength: mb * 1024 * 1024 } as unknown as Float32Array }) as TileData;

interface Deferred {
  load: CancellableLoad;
  resolve: (t: TileData) => void;
  reject: (e: Error) => void;
  cancelled: boolean;
}

const deferred = (): Deferred => {
  const d = { cancelled: false } as Deferred;
  const promise = new Promise<TileData>((res, rej) => {
    d.resolve = res;
    d.reject = rej;
  });
  d.load = {
    promise,
    cancel: () => {
      d.cancelled = true;
      const e = new Error('aborted');
      e.name = 'AbortError';
      d.reject(e);
    },
  };
  return d;
};

const flush = () => new Promise((r) => setTimeout(r, 0));

describe('TileCache cancellation', () => {
  it('aborts loads outside the active set and allows immediate re-request (no back-off)', async () => {
    const cache = new TileCache();
    const started: Deferred[] = [];
    const start = () => {
      const d = deferred();
      started.push(d);
      return d.load;
    };
    let notified = 0;
    cache.subscribe(() => notified++);

    cache.ensure('k', start, () => new Set(['k']));
    expect(started.length).toBe(1);

    cache.abortStale(new Set()); // selection moved on
    expect(started[0].cancelled).toBe(true);
    await flush();
    expect(notified).toBeGreaterThan(0); // abort re-publishes so a wanting effect re-runs

    cache.ensure('k', start, () => new Set(['k'])); // wanted again right away
    expect(started.length).toBe(2);
    started[1].resolve(tile(1));
    await flush();
    expect(cache.has('k')).toBe(true);
  });

  it('does not cancel loads that are still wanted', () => {
    const cache = new TileCache();
    const d = deferred();
    cache.ensure('k', () => d.load, () => new Set(['k']));
    cache.abortStale(new Set(['k']));
    expect(d.cancelled).toBe(false);
  });

  it('keeps failure back-off for real errors', async () => {
    const cache = new TileCache();
    let starts = 0;
    const start = () => {
      starts++;
      const d = deferred();
      d.reject(new Error('boom'));
      return d.load;
    };
    cache.ensure('k', start, () => new Set(['k']));
    await flush();
    expect(cache.getSnapshot().error).toBe('boom');
    cache.ensure('k', start, () => new Set(['k'])); // within back-off window
    expect(starts).toBe(1);
  });
});

describe('TileCache byte budget', () => {
  it('evicts oldest non-active tiles once the budget is exceeded', async () => {
    const cache = new TileCache();
    const put = async (key: string, mb: number, active: string[]) => {
      const d = deferred();
      cache.ensure(key, () => d.load, () => new Set(active));
      d.resolve(tile(mb));
      await flush();
    };

    await put('t1', 150, ['t1']);
    await put('t2', 150, ['t2']);
    expect(cache.has('t1')).toBe(true); // 300MB — under budget, nothing evicted

    await put('t3', 150, ['t3']); // 450MB > 384MB — t1 (oldest, inactive) goes
    expect(cache.has('t1')).toBe(false);
    expect(cache.has('t2')).toBe(true);
    expect(cache.has('t3')).toBe(true);
  });

  it('never evicts active keys, even over budget', async () => {
    const cache = new TileCache();
    const active = ['a', 'b', 'c'];
    for (const key of active) {
      const d = deferred();
      cache.ensure(key, () => d.load, () => new Set(active));
      d.resolve(tile(200));
      await flush();
    }
    expect(active.every((k) => cache.has(k))).toBe(true); // 600MB resident, all on-screen
  });
});
