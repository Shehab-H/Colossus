import { describe, expect, it } from 'vitest';
import { hexToRgb, interpolate } from './colors';
import { categoricalColors, DEFAULT_SEQUENTIAL, sequentialStops } from './schemes';

describe('color primitives', () => {
  it('parses full and short hex', () => {
    expect(hexToRgb('#E69F00')).toEqual([230, 159, 0]);
    expect(hexToRgb('#fff')).toEqual([255, 255, 255]);
    expect(hexToRgb('000000')).toEqual([0, 0, 0]);
  });

  it('interpolates stops and clamps t', () => {
    const stops = [hexToRgb('#000000'), hexToRgb('#ffffff')] as const;
    expect(interpolate(stops, 0)).toEqual([0, 0, 0]);
    expect(interpolate(stops, 1)).toEqual([255, 255, 255]);
    expect(interpolate(stops, 0.5)).toEqual([128, 128, 128]);
    expect(interpolate(stops, -3)).toEqual([0, 0, 0]);
    expect(interpolate(stops, 9)).toEqual([255, 255, 255]);
  });
});

describe('scheme registry', () => {
  it('resolves known schemes and falls back for unknown', () => {
    expect(sequentialStops('viridis')[0]).toEqual([68, 1, 84]);
    expect(sequentialStops('no-such')).toEqual(sequentialStops(DEFAULT_SEQUENTIAL));
  });

  it('ships an 8-color colorblind-safe categorical default', () => {
    expect(categoricalColors('okabeIto')).toHaveLength(8);
    expect(categoricalColors('okabeIto')[0]).toEqual(hexToRgb('#E69F00'));
  });
});
