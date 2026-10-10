import { describe, expect, it } from 'vitest';
import { catalogContentState } from './catalogState';

describe('map management presentation states', () => {
  it.each([
    [null, false, 'loading'],
    [0, true, 'error'],
    [0, false, 'empty'],
    [1, false, 'ready'],
  ] as const)('maps catalog inputs to the %s state', (count, hasError, state) => {
    expect(catalogContentState(count, hasError)).toBe(state);
  });
});
