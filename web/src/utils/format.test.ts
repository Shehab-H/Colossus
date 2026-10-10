import { describe, expect, it } from 'vitest';
import { formatBytes } from './format';

describe('map management presentation states', () => {
  it('keeps missing release sizes explicitly pending', () => {
    expect(formatBytes()).toBe('Pending metadata');
    expect(formatBytes(1_500_000)).toBe('1.5 MB');
  });
});
