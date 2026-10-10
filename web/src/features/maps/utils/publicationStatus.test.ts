import { describe, expect, it } from 'vitest';
import { publicationLabel, publicationTone } from './publicationStatus';

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
});
