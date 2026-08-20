import { describe, expect, it } from 'vitest';
import {
  colorChannelOption,
  defaultScheme,
  measureColorOption,
  previewSpec,
  recommendedColorSpec,
  schemeFamily,
  withCustomRange,
  withScaleType,
} from './colorEncoding';

describe('color encoding authoring', () => {
  it('chooses explicit defaults from channel semantics', () => {
    const numeric = colorChannelOption({ name: 'speed', column: 'speed', role: 'measure', type: 'f32' });
    const category = colorChannelOption({ name: 'operator', column: 'operator', role: 'dimension', type: 'dict' });
    expect(recommendedColorSpec(numeric)).toEqual({ channel: 'speed', type: 'linear', scheme: 'viridis' });
    expect(recommendedColorSpec(category)).toEqual({ channel: 'operator', type: 'categorical', scheme: 'okabeIto' });
    expect(recommendedColorSpec(measureColorOption('total'))).toMatchObject({ type: 'linear', scheme: 'viridis' });
  });

  it('normalizes scale-specific fields and families', () => {
    const base = { channel: 'value', type: 'linear' as const, scheme: 'viridis', bins: 8, overrides: { x: '#fff' } };
    expect(withScaleType(base, 'diverging')).toEqual({ channel: 'value', type: 'diverging', scheme: 'blueRed', midpoint: 0, unknown: undefined });
    expect(withScaleType(base, 'quantile')).toMatchObject({ type: 'quantile', bins: 5, scheme: 'viridis' });
    expect(schemeFamily('categorical')).toBe('categorical');
    expect(defaultScheme('diverging')).toBe('blueRed');
  });

  it('converts presets to editable colors and sanitizes preview-only invalid values', () => {
    expect(withCustomRange({ channel: 'value', type: 'linear', scheme: 'greens' }).range).toHaveLength(5);
    expect(previewSpec({
      channel: 'operator', type: 'categorical', scheme: 'okabeIto', range: ['bad', '#fff'],
      unknown: 'broken', overrides: { A: '#123456', '': '#fff', B: 'bad' },
    })).toMatchObject({ range: undefined, unknown: undefined, overrides: { A: '#123456' } });
  });
});
