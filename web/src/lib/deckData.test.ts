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

  it('a channel rendered through a mismatched categorical LUT never poisons its numeric entry', () => {
    // The domain-fetch gap on a colour switch: the new channel's numeric values render one frame
    // against the previous channel's categories. That buffer (all miss-codes) must not be served
    // once the numeric domain lands — the all-yellow apex_share regression.
    const d = pointTile({ m: new Float32Array([0.5, 1]) }, 2);
    const cat = tileDeckData(d, 'm', ['a', 'b']) as Attrs;
    const num = tileDeckData(d, 'm', null) as Attrs;
    expect(num).not.toBe(cat);
    expect([...cat.attributes.getScaleValue.value]).toEqual([2, 2]); // every value misses the category list
    expect([...num.attributes.getScaleValue.value]).toEqual([0.5, 1]); // raw numeric column, unpoisoned
  });

  it('carries the Phase-1 filter attribute when filterSize is given', () => {
    const d: TileData = { ...pointTile({ m: new Float32Array([1, 2]) }, 2), filterValues: new Float32Array([5, 6]) };
    const { attributes } = tileDeckData(d, 'm', null, 1) as Attrs;
    expect(attributes.getFilterValue).toEqual({ value: d.filterValues, size: 1 });
  });
});

describe('tileDeckData — folded measure override (group regime)', () => {
  it('numeric override supplies the value attribute (folded values, not the baked column)', () => {
    const baked = new Float32Array([1, 2, 3]);
    const d = pointTile({ total_tests: baked }, 3);
    const override = new Float32Array([10, 20, NaN]); // last mark emptied by context → NaN → unknown
    const { attributes } = tileDeckData(d, 'total_tests', null, undefined, override, 'ctx') as Attrs;
    expect([...attributes.getScaleValue.value]).toEqual([10, 20, NaN]);
    expect(attributes.getScaleValue.value).not.toBe(baked);
  });

  it('argmax override codes map through the category domain; ARGMAX_UNKNOWN → the unknown texel', () => {
    const d = pointTile({ dominant_operator: { kind: 'dict', codes: new Uint8Array([0, 0]), dict: ['apex'] } }, 2);
    const override = new Uint16Array([1, 0xffff]); // canonical code 1, then an emptied mark
    const { attributes } = tileDeckData(d, 'dominant_operator', ['apex', 'zenith'], undefined, override, 'ctx') as Attrs;
    expect([...attributes.getScaleValue.value]).toEqual([1, 2]); // 2 == categories.length (unknown texel)
  });

  it('a tile rendered before its fold lands reuses the plain channel entry — it never poisons the context key', () => {
    const d = pointTile({ m: new Float32Array([1, 2]) }, 2);
    const bare = tileDeckData(d, 'm', null);
    expect(tileDeckData(d, 'm', null, undefined, undefined, 'ctx')).toBe(bare); // fold pending → baked entry
    const folded = tileDeckData(d, 'm', null, undefined, new Float32Array([5, 6]), 'ctx') as Attrs;
    expect(folded).not.toBe(bare); // fold arrival changes the key → fresh buffer with the folded values
    expect([...folded.attributes.getScaleValue.value]).toEqual([5, 6]);
  });

  it('the context key separates cached buffers (scrub back reuses without a rebuild)', () => {
    const d = pointTile({ m: new Float32Array([1, 2]) }, 2);
    const a = tileDeckData(d, 'm', null, undefined, new Float32Array([5, 6]), 'ctxA');
    expect(tileDeckData(d, 'm', null, undefined, new Float32Array([5, 6]), 'ctxA')).toBe(a); // same context → cached
    expect(tileDeckData(d, 'm', null, undefined, new Float32Array([7, 8]), 'ctxB')).not.toBe(a); // new context → new buffer
  });
});
