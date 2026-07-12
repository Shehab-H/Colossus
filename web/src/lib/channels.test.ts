import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Manifest, ViewConfig } from './manifest';
import { canonicalCategories, describeColorDomain, discoverOptions, inDateRange, makeDateRange, parseDateRange } from './channels';

// Baked manifest domains must answer WITHOUT touching the tile store; only the legacy fallback
// (old manifests / truncated domains) may fetch the root tile.
vi.mock('./arrow', () => ({ fetchArrowTable: vi.fn(() => Promise.reject(new Error('unexpected tile fetch'))) }));
import { fetchArrowTable } from './arrow';

const view: ViewConfig = {
  id: 'v',
  viewport: 'geo',
  mark: 'point',
  source: {
    adapter: 'test',
    query: '',
    geometry: { kind: 'lonLat' },
    channels: [
      { name: 'pop', column: 'pop', role: 'measure', type: 'f64' },
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

beforeEach(() => {
  vi.mocked(fetchArrowTable).mockClear(); // void body: a returned function would run as a cleanup hook
});

describe('describeColorDomain from baked domains', () => {
  it('numeric channels use min/max and the quantile grid as the sample', async () => {
    const d = await describeColorDomain(
      manifest({ pop: { min: 0, max: 900, quantiles: [0, 10, 900] } }),
      'pop',
    );
    expect(d).toEqual({ kind: 'numeric', min: 0, max: 900, sample: [0, 10, 900] });
    expect(fetchArrowTable).not.toHaveBeenCalled();
  });

  it('categorical channels use the full baked value list', async () => {
    const d = await describeColorDomain(manifest({ cat: { values: ['a', 'b', 'z'] } }), 'cat');
    expect(d).toEqual({ kind: 'categorical', categories: ['a', 'b', 'z'] });
    expect(fetchArrowTable).not.toHaveBeenCalled();
  });

  it('a truncated domain falls back to the root-tile scan', async () => {
    await expect(describeColorDomain(manifest({ cat: { valuesTruncated: true } }), 'cat')).rejects.toThrow(
      'unexpected tile fetch',
    );
    expect(fetchArrowTable).toHaveBeenCalledTimes(1);
  });
});

describe('canonicalCategories', () => {
  it('prefers the baked full-extract domain', () => {
    expect(canonicalCategories(manifest({ cat: { values: ['a', 'b', 'z'] } }), 'cat')).toEqual(['a', 'b', 'z']);
  });

  it('returns null for a truncated or absent domain (caller falls back to options)', () => {
    expect(canonicalCategories(manifest({ cat: { valuesTruncated: true, values: ['a'] } }), 'cat')).toBeNull();
    expect(canonicalCategories(manifest({}), 'cat')).toBeNull();
  });

  it('agrees with the color domain category order (one shared source)', async () => {
    const m = manifest({ cat: { values: ['b', 'a', 'z'] } });
    const canon = canonicalCategories(m, 'cat');
    const color = await describeColorDomain(m, 'cat');
    expect(color).toEqual({ kind: 'categorical', categories: canon });
  });
});

describe('discoverOptions from baked domains', () => {
  it('answers every filterable channel without a fetch when domains cover them', async () => {
    const o = await discoverOptions(
      manifest({ cat: { values: ['x', 'y'] }, day: { values: ['2024-01-01', '2024-06-01'] } }),
    );
    expect(o).toEqual({ cat: ['x', 'y'], day: ['2024-01-01', '2024-06-01'] });
    expect(fetchArrowTable).not.toHaveBeenCalled();
  });

  it('fetches the root tile only when some channel is missing a usable domain', async () => {
    await expect(discoverOptions(manifest({ cat: { values: ['x'] } }))).rejects.toThrow('unexpected tile fetch');
    expect(fetchArrowTable).toHaveBeenCalledTimes(1);
  });
});

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
