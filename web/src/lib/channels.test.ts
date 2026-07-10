import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Manifest, ViewConfig } from './manifest';
import { describeColorDomain, discoverOptions } from './channels';

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
