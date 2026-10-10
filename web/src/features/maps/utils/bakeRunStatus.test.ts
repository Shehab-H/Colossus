import { describe, expect, it } from 'vitest';
import type { BakeRunCounts } from '@/types/bake';
import { runFilterCount, runFilterStatuses } from './bakeRunStatus';

const counts: BakeRunCounts = { queued: 1, running: 2, succeeded: 30, failed: 14, cancelled: 3 };

describe('bake run history filters', () => {
  it.each([
    ['all', 50],
    ['active', 3],
    ['failed', 14],
    ['succeeded', 30],
    ['cancelled', 3],
  ] as const)('counts %s runs from the server totals', (filter, total) => {
    expect(runFilterCount(filter, counts)).toBe(total);
  });

  it('asks the server for every status when unfiltered', () => {
    expect(runFilterStatuses('all')).toEqual([]);
    expect(runFilterStatuses('active')).toEqual(['queued', 'running']);
  });
});
