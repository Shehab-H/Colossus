import { describe, expect, it } from 'vitest';
import type { ColorDomain } from './scale';
import { describeLegend } from './legend';
import { sequentialStops } from './schemes';

const numeric = (min: number, max: number, sample?: number[]): ColorDomain => ({ kind: 'numeric', min, max, sample });
const categorical = (categories: string[]): ColorDomain => ({ kind: 'categorical', categories });

describe('legend descriptor', () => {
  it('describes a continuous scale as a gradient with axis labels', () => {
    const l = describeLegend({ channel: 'v', type: 'linear', scheme: 'viridis' }, numeric(0, 100), 'v')!;
    expect(l.kind).toBe('continuous');
    expect(l.note).toBe('gradient');
    expect(l.gradient).toEqual(sequentialStops('viridis'));
    expect(l.min).toBe(0);
    expect(l.max).toBe(100);
    expect(l.midpoint).toBeUndefined();
  });

  it('flags clamped ends when robust bounds cut off outliers', () => {
    const sample = Array.from({ length: 101 }, (_, i) => i);
    const l = describeLegend({ channel: 'v' }, numeric(-500, 10000, sample), 'v')!;
    expect(l.min).toBeCloseTo(2);
    expect(l.max).toBeCloseTo(98);
    expect(l.minClamped).toBe(true);
    expect(l.maxClamped).toBe(true);
    // No sample → no clamping flags.
    const raw = describeLegend({ channel: 'v' }, numeric(0, 100), 'v')!;
    expect(raw.minClamped).toBeUndefined();
    expect(raw.maxClamped).toBeUndefined();
  });

  it('marks the midpoint for a diverging scale', () => {
    const l = describeLegend({ channel: 'v', type: 'diverging', midpoint: 0 }, numeric(-10, 10), 'v')!;
    expect(l.note).toBe('diverging');
    expect(l.midpoint).toBe(0);
  });

  it('lists one labelled swatch per bucket for a binned scale', () => {
    const l = describeLegend({ channel: 'v', type: 'quantize', bins: 3 }, numeric(0, 90), 'v')!;
    expect(l.kind).toBe('binned');
    expect(l.items).toHaveLength(3);
    expect(l.items![0].label.startsWith('<')).toBe(true);
    expect(l.items![2].label.startsWith('≥')).toBe(true);
  });

  it('lists categories and an "other" swatch for a closed palette', () => {
    const l = describeLegend(
      { channel: 'op', type: 'categorical', palette: { Vodafone: '#e60000', Orange: '#ff7900' }, unknown: '#888888' },
      categorical(['Vodafone', 'Orange']),
      'op',
    )!;
    expect(l.kind).toBe('categorical');
    expect(l.items!.map((i) => i.label)).toEqual(['Vodafone', 'Orange', 'other']);
  });
});
