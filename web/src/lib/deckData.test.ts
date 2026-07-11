import { describe, expect, it } from 'vitest';
import { tileDeckData } from './deckData';
import type { TileData } from './tileData';

type Attrs = {
  attributes: { getScaleValue: { value: Float32Array; size: number }; getFilterValue?: { value: Float32Array; size: number } };
};

const pointTile = (values: TileData['values'], count: number): TileData => ({
  count,
  positions: new Float32Array(count * 2),
  values,
});

describe('tileDeckData — numeric value attribute', () => {
  it('uses the resident f32 column by reference (zero copy)', () => {
    const col = new Float32Array([7, 8, 9]);
    const d = pointTile({ m: col }, 3);
    const { attributes } = tileDeckData(d, 'm', null) as Attrs;
    expect(attributes.getScaleValue.value).toBe(col); // same object, not a copy
    expect(attributes.getScaleValue.size).toBe(1);
  });

  it('a missing channel becomes NaN (the shader maps NaN → unknown color)', () => {
    const d = pointTile({}, 2);
    const { attributes } = tileDeckData(d, 'nope', null) as Attrs;
    expect([...attributes.getScaleValue.value].every((v) => Number.isNaN(v))).toBe(true);
  });
});

describe('tileDeckData — categorical codes', () => {
  it('maps each mark to its canonical code; out-of-domain → the trailing unknown texel', () => {
    // canonical ['x','y'] → codes 0,1; 'z' is out-of-domain → code 2 (== categories.length)
    const d = pointTile({ c: { kind: 'dict', codes: new Uint8Array([0, 1, 2]), dict: ['x', 'y', 'z'] } }, 3);
    const { attributes } = tileDeckData(d, 'c', ['x', 'y']) as Attrs;
    expect([...attributes.getScaleValue.value]).toEqual([0, 1, 2]);
  });

  it('a missing categorical channel is all-unknown', () => {
    const d = pointTile({}, 2);
    const { attributes } = tileDeckData(d, 'c', ['x', 'y']) as Attrs;
    expect([...attributes.getScaleValue.value]).toEqual([2, 2]);
  });
});

describe('tileDeckData — polygons', () => {
  it('expands per-mark values across each ring', () => {
    const d: TileData = {
      count: 2,
      polyPositions: new Float32Array(8),
      polyStartIndices: new Uint32Array([0, 2, 4]),
      vertexCount: 4,
      values: { c: { kind: 'dict', codes: new Uint8Array([0, 1]), dict: ['x', 'y'] } },
    };
    const { attributes } = tileDeckData(d, 'c', ['x', 'y']) as Attrs;
    expect([...attributes.getScaleValue.value]).toEqual([0, 0, 1, 1]);
  });
});

describe('tileDeckData — caching and filter attribute', () => {
  it('memoizes per (tile, channel) — stable identity across recolor (scale is GPU state now)', () => {
    const d = pointTile({ m: new Float32Array([1]), n: new Float32Array([2]) }, 1);
    const a = tileDeckData(d, 'm', null);
    expect(tileDeckData(d, 'm', null)).toBe(a); // same channel → same object
    expect(tileDeckData(d, 'n', null)).not.toBe(a); // different channel → different object
  });

  it('carries the Phase-1 filter attribute when filterSize is given', () => {
    const d: TileData = { ...pointTile({ m: new Float32Array([1, 2]) }, 2), filterValues: new Float32Array([5, 6]) };
    const { attributes } = tileDeckData(d, 'm', null, 1) as Attrs;
    expect(attributes.getFilterValue).toEqual({ value: d.filterValues, size: 1 });
  });
});
