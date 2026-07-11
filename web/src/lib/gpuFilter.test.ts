import { describe, expect, it } from 'vitest';
import type { Manifest, ViewConfig } from './manifest';
import {
  MAX_SAFE_F32,
  MISSING_CODE,
  NULL_DAY,
  anyActive,
  buildFilterValues,
  canonicalCodeLut,
  dayNumber,
  dayNumberOfIso,
  filterRanges,
  filterSlots,
  type FilterSlots,
} from './gpuFilter';

const view: ViewConfig = {
  id: 'v',
  viewport: 'geo',
  mark: 'point',
  source: {
    adapter: 'test',
    query: '',
    geometry: { kind: 'lonLat' },
    channels: [
      { name: 'pop', column: 'pop', role: 'measure', type: 'f64' }, // not filterable
      { name: 'cat', column: 'cat', role: 'dimension', type: 'dict' },
      { name: 'day', column: 'day', role: 'temporal', type: 'date' },
    ],
  },
};

const manifest = (domains: Manifest['channelDomains']): Manifest => ({
  version: 'v1',
  view,
  reduction: 'quadtreeLod',
  regime: 'large',
  root: { minX: 0, minY: 0, maxX: 1, maxY: 1 },
  minZoom: 0,
  maxZoom: 0,
  tilePointBudget: 0,
  totalPoints: 0,
  tiles: [],
  channelDomains: domains,
});

describe('filterSlots', () => {
  it('one slot per filterable channel, in channel order, size = count', () => {
    const s = filterSlots(manifest({ cat: { values: ['a', 'b'] } }))!;
    expect(s.specs.map((x) => x.name)).toEqual(['cat', 'day']); // measure 'pop' excluded, order preserved
    expect(s.specs.map((x) => x.kind)).toEqual(['dimension', 'temporal']);
    expect(s.size).toBe(2);
  });

  it('dimension slot carries the canonical category order (baked domain preferred, else options)', () => {
    expect(filterSlots(manifest({ cat: { values: ['a', 'b'] } }))!.specs[0].categories).toEqual(['a', 'b']);
    // no baked domain → falls back to the discovered option list (same array the UI shows)
    expect(filterSlots(manifest({}), { cat: ['x', 'y'] })!.specs[0].categories).toEqual(['x', 'y']);
  });

  it('null when there are no filterable channels', () => {
    const measuresOnly: ViewConfig = { ...view, source: { ...view.source, channels: [view.source.channels[0]] } };
    expect(filterSlots({ ...manifest({}), view: measuresOnly })).toBeNull();
  });
});

describe('filterRanges', () => {
  const slots: FilterSlots = {
    specs: [{ name: 'cat', kind: 'dimension', categories: ['a', 'b', 'c'] }, { name: 'day', kind: 'temporal' }],
    size: 2,
  };

  it('dimension equality → [code, code]; slots stay in order', () => {
    expect(filterRanges(slots, { cat: 'b' })).toEqual([[1, 1], [-MAX_SAFE_F32, MAX_SAFE_F32]]);
    expect(filterRanges(slots, { cat: 'c' })[0]).toEqual([2, 2]);
  });

  it('a value not in the canonical list matches nothing → [-2, -2]', () => {
    expect(filterRanges(slots, { cat: 'zzz' })[0]).toEqual([-2, -2]);
  });

  it('(all) / empty / missing → wide-open', () => {
    expect(filterRanges(slots, {})[0]).toEqual([-MAX_SAFE_F32, MAX_SAFE_F32]);
    expect(filterRanges(slots, { cat: '(all)' })[0]).toEqual([-MAX_SAFE_F32, MAX_SAFE_F32]);
    expect(filterRanges(slots, { cat: '' })[0]).toEqual([-MAX_SAFE_F32, MAX_SAFE_F32]);
  });

  it('temporal from..to and open-ended sides use the ± sentinel', () => {
    expect(filterRanges(slots, { day: '2024-01-01..2024-06-01' })[1]).toEqual([dayNumberOfIso('2024-01-01'), dayNumberOfIso('2024-06-01')]);
    expect(filterRanges(slots, { day: '2024-01-01..' })[1]).toEqual([dayNumberOfIso('2024-01-01'), MAX_SAFE_F32]);
    expect(filterRanges(slots, { day: '..2024-06-01' })[1]).toEqual([-MAX_SAFE_F32, dayNumberOfIso('2024-06-01')]);
    expect(filterRanges(slots, { day: '2024-01-01' })[1]).toEqual([dayNumberOfIso('2024-01-01'), dayNumberOfIso('2024-01-01')]); // legacy single day
  });

  it('anyActive is false only when every slot is wide-open', () => {
    expect(anyActive(filterRanges(slots, {}))).toBe(false);
    expect(anyActive(filterRanges(slots, { cat: 'a' }))).toBe(true);
    expect(anyActive(filterRanges(slots, { day: '2024-01-01..' }))).toBe(true);
  });
});

describe('null-temporal sentinel ordering (reproduces isoDate("null") lexicographic behavior)', () => {
  it('NULL_DAY passes any from bound but fails any finite to (only an open to keeps it)', () => {
    const nul = dayNumber(null);
    expect(nul).toBe(NULL_DAY);
    const from = dayNumberOfIso('2024-01-01');
    const to = dayNumberOfIso('2024-06-01');
    expect(nul >= from).toBe(true); // passes lower bound
    expect(nul <= to).toBe(false); // fails a finite upper bound → filtered out
    expect(nul <= MAX_SAFE_F32).toBe(true); // open upper (MAX_SAFE_F32 > NULL_DAY) keeps it
  });
});

describe('dayNumber heuristic parity with isoDate storage', () => {
  const isoOf = (dayNum: number) => new Date(dayNum * 86400000).toISOString().slice(0, 10);

  it('day-count storage: |v| < 1e7 is a day count', () => {
    expect(dayNumber(19723)).toBe(19723);
    expect(isoOf(dayNumber(19723))).toBe('2024-01-01');
    expect(dayNumber(0)).toBe(0);
    expect(isoOf(dayNumber(0))).toBe('1970-01-01');
  });

  it('epoch-millis storage: |v| ≥ 1e7 is milliseconds', () => {
    expect(dayNumber(19723 * 86400000)).toBe(19723);
  });

  it('Date objects and ISO strings agree with the numeric path', () => {
    expect(dayNumber(new Date(Date.UTC(2024, 0, 1)))).toBe(19723);
    expect(dayNumber('2024-01-01')).toBe(19723);
    expect(dayNumberOfIso('2024-01-01')).toBe(19723);
  });

  it('null / NaN / unparseable → NULL_DAY', () => {
    expect(dayNumber(null)).toBe(NULL_DAY);
    expect(dayNumber(undefined)).toBe(NULL_DAY);
    expect(dayNumber('not-a-date')).toBe(NULL_DAY);
    expect(dayNumberOfIso('garbage')).toBe(NULL_DAY);
  });
});

describe('canonicalCodeLut', () => {
  it('maps local dict codes to the canonical order, missing → MISSING_CODE', () => {
    // local dict ['b','a','c'] against canonical ['a','b'] → [1, 0, MISSING]. Float32Array compare so
    // the sentinel is f32-rounded on both sides.
    expect(canonicalCodeLut(['b', 'a', 'c'], ['a', 'b'])).toEqual(Float32Array.from([1, 0, MISSING_CODE]));
  });

  it('unknown categories → all MISSING_CODE', () => {
    expect(canonicalCodeLut(['a', 'b'], undefined)).toEqual(Float32Array.from([MISSING_CODE, MISSING_CODE]));
  });
});

describe('buildFilterValues', () => {
  it('points: interleaves per-mark slot values in slot order', () => {
    const s0 = new Float32Array([0, 1, 2]);
    const s1 = new Float32Array([10, 11, 12]);
    expect([...buildFilterValues(2, 3, [s0, s1])]).toEqual([0, 10, 1, 11, 2, 12]);
  });

  it('polygons: expands each mark value across its ring vertices via polyStartIndices', () => {
    // mark 0 → 4 verts, mark 1 → 3 verts
    const start = new Uint32Array([0, 4, 7]);
    const s0 = new Float32Array([5, 9]);
    const out = buildFilterValues(1, 2, [s0], start, 7);
    expect([...out]).toEqual([5, 5, 5, 5, 9, 9, 9]);
  });

  it('polygons with two slots interleave per vertex', () => {
    const start = new Uint32Array([0, 2, 3]);
    const s0 = new Float32Array([1, 2]);
    const s1 = new Float32Array([7, 8]);
    // mark0 (2 verts): [1,7,1,7]; mark1 (1 vert): [2,8]
    expect([...buildFilterValues(2, 2, [s0, s1], start, 3)]).toEqual([1, 7, 1, 7, 2, 8]);
  });
});
