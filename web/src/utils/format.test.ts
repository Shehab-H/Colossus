import { describe, expect, it } from 'vitest';
import { formatBytes, formatDuration } from './format';

describe('map management presentation states', () => {
  it('keeps missing release sizes explicitly pending', () => {
    expect(formatBytes()).toBe('Pending metadata');
    expect(formatBytes(1_500_000)).toBe('1.5 MB');
  });

  it('formats run durations and leaves unfinished runs blank', () => {
    expect(formatDuration('2026-10-10T10:00:00Z', '2026-10-10T10:00:42Z')).toBe('42s');
    expect(formatDuration('2026-10-10T10:00:00Z', '2026-10-10T10:03:05Z')).toBe('3m 5s');
    expect(formatDuration('2026-10-10T10:00:00Z', '2026-10-10T12:15:00Z')).toBe('2h 15m');
    expect(formatDuration('2026-10-10T10:00:00Z')).toBe('—');
  });
});
