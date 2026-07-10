import { describe, expect, it } from 'vitest';
import type { ColorFn } from './colorScale';
import { tileDeckData } from './deckData';
import type { TileData } from './tileData';

type Attrs = { attributes: { getFillColor: { value: Uint8Array } } };

const colorOf: ColorFn = (v) => {
  if (v === 'x') return [1, 2, 3];
  if (v === 'y') return [4, 5, 6];
  if (typeof v === 'number') return [v, 0, 0];
  return [9, 9, 9];
};

const pointTile = (values: TileData['values'], count: number): TileData => ({
  count,
  positions: new Float32Array(count * 2),
  values,
});

describe('tileDeckData colors', () => {
  it('dict LUT path matches per-row colorOf output', () => {
    const d = pointTile({ c: { kind: 'dict', codes: new Uint8Array([0, 1, 0]), dict: ['x', 'y'] } }, 3);
    const { attributes } = tileDeckData(d, 'c', colorOf, 's') as Attrs;
    expect([...attributes.getFillColor.value]).toEqual([1, 2, 3, 4, 5, 6, 1, 2, 3]);
  });

  it('numeric columns run the scale per mark', () => {
    const d = pointTile({ m: new Float32Array([7, 8]) }, 2);
    const { attributes } = tileDeckData(d, 'm', colorOf, 's') as Attrs;
    expect([...attributes.getFillColor.value]).toEqual([7, 0, 0, 8, 0, 0]);
  });

  it('missing channel fills with the unknown color', () => {
    const d = pointTile({}, 2);
    const { attributes } = tileDeckData(d, 'nope', colorOf, 's') as Attrs;
    expect([...attributes.getFillColor.value]).toEqual([9, 9, 9, 9, 9, 9]);
  });

  it('memoizes per (tile, channel, scaleKey)', () => {
    const d = pointTile({ m: new Float32Array([1]) }, 1);
    const a = tileDeckData(d, 'm', colorOf, 's1');
    expect(tileDeckData(d, 'm', colorOf, 's1')).toBe(a);
    expect(tileDeckData(d, 'm', colorOf, 's2')).not.toBe(a);
  });

  it('expands per-mark colors across each polygon ring', () => {
    const d: TileData = {
      count: 2,
      polyPositions: new Float32Array(8),
      polyStartIndices: new Uint32Array([0, 2, 4]),
      vertexCount: 4,
      values: { c: { kind: 'dict', codes: new Uint8Array([0, 1]), dict: ['x', 'y'] } },
    };
    const { attributes } = tileDeckData(d, 'c', colorOf, 's') as Attrs;
    expect([...attributes.getFillColor.value]).toEqual([1, 2, 3, 1, 2, 3, 4, 5, 6, 4, 5, 6]);
  });
});
