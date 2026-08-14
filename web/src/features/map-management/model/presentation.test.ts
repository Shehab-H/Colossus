import { describe, expect, it } from 'vitest';
import { catalogContentState, formatBytes, publicationLabel, publicationTone } from './presentation';

describe('map management presentation states', () => {
  it.each([
    ['neverBaked', 'Never baked', 'neutral'],
    ['queued', 'Queued', 'progress'],
    ['baking', 'Baking', 'progress'],
    ['published', 'Published', 'success'],
    ['lastRunFailed', 'Last run failed', 'danger'],
    ['archived', 'Archived', 'neutral'],
  ] as const)('maps %s without inventing transport state', (state, label, tone) => {
    expect(publicationLabel(state)).toBe(label);
    expect(publicationTone(state)).toBe(tone);
  });

  it('keeps missing release sizes explicitly pending', () => {
    expect(formatBytes()).toBe('Pending metadata');
    expect(formatBytes(1_500_000)).toBe('1.5 MB');
  });

  it.each([
    [null, false, 'loading'],
    [0, true, 'error'],
    [0, false, 'empty'],
    [1, false, 'ready'],
  ] as const)('maps catalog inputs to the %s state', (count, hasError, state) => {
    expect(catalogContentState(count, hasError)).toBe(state);
  });
});
