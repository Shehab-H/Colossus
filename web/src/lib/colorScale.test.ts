import { describe, expect, it } from 'vitest';
import { buildColorScale, categoryKey, describeLegend, inferType, type ColorDomain } from './colorScale';
import { hexToRgb } from './colors';
import { sequentialStops } from './schemes';
import type { ColorSpec } from './manifest';

const numeric = (min: number, max: number, sample?: number[]): ColorDomain => ({ kind: 'numeric', min, max, sample });
const categorical = (categories: string[]): ColorDomain => ({ kind: 'categorical', categories });

describe('inferType', () => {
  it('defaults numeric to linear and categorical domains to categorical', () => {
    expect(inferType({ channel: 'v' }, numeric(0, 1))).toBe('linear');
    expect(inferType({ channel: 'op' }, categorical(['a']))).toBe('categorical');
  });
  it('infers from spec shape', () => {
    expect(inferType({ channel: 'v', palette: { a: '#fff' } }, numeric(0, 1))).toBe('categorical');
    expect(inferType({ channel: 'v', thresholds: [1] }, numeric(0, 1))).toBe('threshold');
    expect(inferType({ channel: 'v', type: 'diverging' }, numeric(0, 1))).toBe('diverging');
  });
});

describe('continuous scales', () => {
  it('linear maps domain ends to ramp ends', () => {
    const f = buildColorScale({ channel: 'v', type: 'linear', scheme: 'viridis' }, numeric(0, 100));
    const stops = sequentialStops('viridis');
    expect(f(0)).toEqual(stops[0]);
    expect(f(100)).toEqual(stops[stops.length - 1]);
  });

  it('reverse flips the ramp', () => {
    const spec: ColorSpec = { channel: 'v', type: 'linear', scheme: 'viridis' };
    const f = buildColorScale(spec, numeric(0, 100));
    const r = buildColorScale({ ...spec, reverse: true }, numeric(0, 100));
    expect(r(0)).toEqual(f(100));
  });

  it('explicit range overrides the scheme', () => {
    const f = buildColorScale({ channel: 'v', range: ['#000000', '#ffffff'] }, numeric(0, 10));
    expect(f(0)).toEqual([0, 0, 0]);
    expect(f(10)).toEqual([255, 255, 255]);
    expect(f(5)).toEqual([128, 128, 128]);
  });

  it('diverging is neutral at the midpoint and opposite at the poles', () => {
    const f = buildColorScale({ channel: 'v', type: 'diverging', scheme: 'blueRed', midpoint: 0 }, numeric(-10, 10));
    expect(f(0)).toEqual(hexToRgb('#f0efec')); // neutral gray middle stop
    expect(f(-10)).toEqual(hexToRgb('#2a78d6'));
    expect(f(10)).toEqual(hexToRgb('#e34948'));
  });

  it('log keeps low values from dominating', () => {
    const lin = buildColorScale({ channel: 'v', type: 'linear', range: ['#000000', '#ffffff'] }, numeric(1, 1000));
    const log = buildColorScale({ channel: 'v', type: 'log', range: ['#000000', '#ffffff'] }, numeric(1, 1000));
    // At value 10, log is far brighter than linear (which is still near-black).
    expect(log(10)[0]).toBeGreaterThan(lin(10)[0]);
  });

  it('defaults to robust p02..p98 bounds when the domain has a sample, clamping outliers', () => {
    const sample = Array.from({ length: 101 }, (_, i) => i); // 0..100; the 10000 max is an outlier
    const f = buildColorScale({ channel: 'v', range: ['#000000', '#ffffff'] }, numeric(0, 10000, sample));
    expect(f(50)).toEqual([128, 128, 128]); // mid-sample sits mid-ramp, not crushed near black
    expect(f(98)).toEqual([255, 255, 255]);
    expect(f(10000)).toEqual([255, 255, 255]); // outlier clamps to the top color
  });

  it('an authored domain overrides the robust default', () => {
    const sample = Array.from({ length: 101 }, (_, i) => i);
    const f = buildColorScale({ channel: 'v', domain: [0, 10000], range: ['#000000', '#ffffff'] }, numeric(0, 10000, sample));
    expect(f(50)[0]).toBeLessThan(5); // full-range linear: 50/10000 is near-black again
  });

  it('without a sample, raw min/max still apply', () => {
    const f = buildColorScale({ channel: 'v', range: ['#000000', '#ffffff'] }, numeric(0, 100));
    expect(f(100)).toEqual([255, 255, 255]);
    expect(f(50)).toEqual([128, 128, 128]);
  });
});

describe('binned scales', () => {
  it('quantize buckets equal-width ranges into discrete colors', () => {
    const f = buildColorScale({ channel: 'v', type: 'quantize', bins: 4, range: ['#000000', '#ffffff'] }, numeric(0, 100));
    // Same bucket → identical color; different bucket → different color.
    expect(f(10)).toEqual(f(24));
    expect(f(10)).not.toEqual(f(40));
  });

  it('threshold uses explicit breakpoints', () => {
    const f = buildColorScale({ channel: 'v', type: 'threshold', thresholds: [50], range: ['#000000', '#ffffff'] }, numeric(0, 100));
    expect(f(49)).toEqual([0, 0, 0]);
    expect(f(50)).toEqual([255, 255, 255]);
  });

  it('quantile splits by data distribution', () => {
    const sample = [1, 2, 3, 4, 100]; // skewed
    const f = buildColorScale({ channel: 'v', type: 'quantile', bins: 2, range: ['#000000', '#ffffff'] }, numeric(1, 100, sample));
    expect(f(1)).not.toEqual(f(100));
  });
});

describe('categorical scales across datatypes', () => {
  it('maps an explicit palette and falls back for the unmapped', () => {
    const f = buildColorScale(
      { channel: 'op', type: 'categorical', palette: { Vodafone: '#e60000', Orange: '#ff7900' }, unknown: '#123456' },
      categorical(['Vodafone', 'Orange', 'Three']),
    );
    expect(f('Vodafone')).toEqual(hexToRgb('#e60000'));
    expect(f('Orange')).toEqual(hexToRgb('#ff7900'));
    expect(f('Three')).toEqual(hexToRgb('#123456')); // unmapped
    expect(f(null)).toEqual(hexToRgb('#123456')); // null
  });

  it('auto-assigns categories in fixed palette order', () => {
    const f = buildColorScale({ channel: 'op', type: 'categorical', scheme: 'okabeIto' }, categorical(['a', 'b', 'c']));
    expect(f('a')).toEqual(hexToRgb('#E69F00'));
    expect(f('b')).toEqual(hexToRgb('#56B4E9'));
    expect(f('c')).toEqual(hexToRgb('#009E73'));
  });

  it('colors numbers and booleans as categories, not just strings', () => {
    const f = buildColorScale(
      { channel: 'band', type: 'categorical', palette: { '5': '#ff0000', true: '#00ff00' } },
      categorical(['5', 'true']),
    );
    expect(f(5)).toEqual([255, 0, 0]); // numeric value keyed as "5"
    expect(f(true as unknown as string)).toEqual([0, 255, 0]);
  });

  it('categoryKey stringifies any type and separates null', () => {
    expect(categoryKey(5)).toBe('5');
    expect(categoryKey('x')).toBe('x');
    expect(categoryKey(null)).not.toBe(categoryKey('null'));
  });
});

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
