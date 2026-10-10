import { describe, expect, it } from 'vitest';
import { inDateRange, makeDateRange, parseDateRange } from './filterValue';

describe('temporal range filter values', () => {
  it('parses from..to, open bounds, and legacy single dates', () => {
    expect(parseDateRange('2024-01-01..2024-06-01')).toEqual({ from: '2024-01-01', to: '2024-06-01' });
    expect(parseDateRange('2024-01-01..')).toEqual({ from: '2024-01-01', to: '' });
    expect(parseDateRange('..2024-06-01')).toEqual({ from: '', to: '2024-06-01' });
    expect(parseDateRange('2024-01-01')).toEqual({ from: '2024-01-01', to: '2024-01-01' }); // legacy single day
  });

  it('treats empty, ALL, and both-empty ranges as no predicate', () => {
    expect(parseDateRange(undefined)).toBeNull();
    expect(parseDateRange('')).toBeNull();
    expect(parseDateRange('(all)')).toBeNull();
    expect(parseDateRange('..')).toBeNull();
  });

  it('round-trips through makeDateRange, collapsing an empty pair to ALL', () => {
    expect(makeDateRange('2024-01-01', '2024-06-01')).toBe('2024-01-01..2024-06-01');
    expect(makeDateRange('2024-01-01', '')).toBe('2024-01-01..');
    expect(makeDateRange('', '2024-06-01')).toBe('..2024-06-01');
    expect(makeDateRange('', '')).toBe('(all)');
  });

  it('inDateRange is inclusive and honors open bounds', () => {
    const r = { from: '2024-01-01', to: '2024-06-01' };
    expect(inDateRange('2024-03-01', r)).toBe(true);
    expect(inDateRange('2024-01-01', r)).toBe(true); // lower bound inclusive
    expect(inDateRange('2024-06-01', r)).toBe(true); // upper bound inclusive
    expect(inDateRange('2023-12-31', r)).toBe(false);
    expect(inDateRange('2024-07-01', { from: '2024-01-01', to: '' })).toBe(true); // open upper bound
    expect(inDateRange('2023-01-01', { from: '', to: '2024-06-01' })).toBe(true); // open lower bound
  });
});
