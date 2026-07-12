import { describe, expect, it } from 'vitest';
import { buildColorLut } from './colorLut';
import { buildColorScale, type ColorDomain } from './colorScale';
import type { ColorSpec } from './manifest';

const numeric = (min: number, max: number, sample?: number[]): ColorDomain => ({ kind: 'numeric', min, max, sample });
const categorical = (categories: string[]): ColorDomain => ({ kind: 'categorical', categories });

const texel = (lut: { texels: Uint8Array }, i: number): [number, number, number] => [
  lut.texels[i * 4],
  lut.texels[i * 4 + 1],
  lut.texels[i * 4 + 2],
];

// The value a numeric LUT texel samples `colorOf` at — the inverse of the shader's t→value mapping.
const texelValue = (lut: { domain: [number, number]; transform: 0 | 1; width: number }, i: number): number => {
  const t = i / (lut.width - 1);
  const v = lut.domain[0] + t * (lut.domain[1] - lut.domain[0]);
  return lut.transform === 1 ? Math.exp(v) : v;
};

describe('buildColorLut — numeric parity with buildColorScale', () => {
  it('every texel equals colorOf evaluated at that texel value (linear)', () => {
    const spec: ColorSpec = { channel: 'v', type: 'linear', range: ['#000000', '#ffffff'] };
    const dom = numeric(0, 100);
    const lut = buildColorLut(spec, dom);
    const colorOf = buildColorScale(spec, dom);
    expect(lut.kind).toBe('numeric');
    expect(lut.transform).toBe(0);
    expect(lut.width).toBe(1024);
    for (const i of [0, 1, 250, 512, 1023]) expect(texel(lut, i)).toEqual(colorOf(texelValue(lut, i)));
  });

  it('log scales sample in log space and stay in parity', () => {
    const spec: ColorSpec = { channel: 'v', type: 'log', range: ['#000000', '#ffffff'] };
    const dom = numeric(1, 1000);
    const lut = buildColorLut(spec, dom);
    const colorOf = buildColorScale(spec, dom);
    expect(lut.transform).toBe(1);
    expect(lut.domain).toEqual([Math.log(1), Math.log(1000)]);
    for (const i of [0, 100, 512, 1023]) expect(texel(lut, i)).toEqual(colorOf(texelValue(lut, i)));
  });

  it('diverging bakes its two arms into a value-space LUT', () => {
    const spec: ColorSpec = { channel: 'v', type: 'diverging', scheme: 'blueRed', midpoint: 0 };
    const dom = numeric(-10, 10);
    const lut = buildColorLut(spec, dom);
    const colorOf = buildColorScale(spec, dom);
    expect(lut.transform).toBe(0);
    for (const i of [0, 256, 512, 768, 1023]) expect(texel(lut, i)).toEqual(colorOf(texelValue(lut, i)));
  });

  it('robust bounds flow through: the LUT domain matches the resolved ramp, not the raw extent', () => {
    const sample = Array.from({ length: 101 }, (_, i) => i);
    const spec: ColorSpec = { channel: 'v', range: ['#000000', '#ffffff'] };
    const dom = numeric(0, 10000, sample);
    const lut = buildColorLut(spec, dom);
    expect(lut.domain[0]).toBeCloseTo(2, 0); // p02
    expect(lut.domain[1]).toBeCloseTo(98, 0); // p98, not 10000
  });
});

describe('buildColorLut — banded scales place edges within one texel', () => {
  it('threshold edge lands within one texel of the true break', () => {
    const spec: ColorSpec = { channel: 'v', type: 'threshold', thresholds: [50], range: ['#000000', '#ffffff'] };
    const dom = numeric(0, 100);
    const lut = buildColorLut(spec, dom);
    // Find the first texel that flips to white; its value must be within one texel-width of 50.
    let edge = -1;
    for (let i = 0; i < lut.width; i++) if (texel(lut, i)[0] > 127) { edge = i; break; }
    const texelWidth = (dom.max - dom.min) / (lut.width - 1);
    expect(Math.abs(texelValue(lut, edge) - 50)).toBeLessThanOrEqual(texelWidth);
  });
});

describe('buildColorLut — categorical', () => {
  it('one texel per category in canonical order, plus a trailing unknown texel', () => {
    const spec: ColorSpec = { channel: 'op', type: 'categorical', scheme: 'okabeIto' };
    const dom = categorical(['a', 'b', 'c']);
    const lut = buildColorLut(spec, dom);
    const colorOf = buildColorScale(spec, dom);
    expect(lut.kind).toBe('categorical');
    expect(lut.categories).toEqual(['a', 'b', 'c']);
    expect(lut.width).toBe(4); // 3 categories + unknown
    expect(texel(lut, 0)).toEqual(colorOf('a'));
    expect(texel(lut, 1)).toEqual(colorOf('b'));
    expect(texel(lut, 2)).toEqual(colorOf('c'));
    expect(texel(lut, 3)).toEqual(lut.unknown); // trailing unknown texel
  });

  it('the unknown texel matches the scale unknown color and colorOf(out-of-domain)', () => {
    const spec: ColorSpec = { channel: 'op', type: 'categorical', palette: { Vodafone: '#e60000' }, unknown: '#123456' };
    const dom = categorical(['Vodafone']);
    const lut = buildColorLut(spec, dom);
    const colorOf = buildColorScale(spec, dom);
    expect(lut.unknown).toEqual(colorOf(null));
    expect(texel(lut, lut.width - 1)).toEqual(colorOf('not-a-known-value'));
  });
});
