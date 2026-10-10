import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Manifest } from '@/types/manifest';

// Baked manifest domains must answer WITHOUT touching the tile store; only the legacy fallback
// (old manifests / truncated domains) may fetch the root tile.
vi.mock('../network/arrowFetch', () => ({ fetchArrowTable: vi.fn(() => Promise.reject(new Error('unexpected tile fetch'))) }));
import { fetchArrowTable } from '../network/arrowFetch';
import type { ViewConfig } from '@/types/viewConfig';
import { discoverOptions } from './discoverOptions';
import { filterableChannels } from './filterRouting';

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


describe('authored filter curation', () => {
  it('keeps automatic controls only when filter configuration is omitted', () => {
    expect(filterableChannels(view).map((channel) => channel.name)).toEqual(['cat', 'day']);
    expect(filterableChannels({ ...view, filters: [] })).toEqual([]);
  });

  it('uses numeric min/max as the two input bounds for an explicit range', async () => {
    const ranged = { ...view, filters: [{ channel: 'pop', control: 'range' as const }] };
    const o = await discoverOptions({ ...manifest({ pop: { min: -12.5, max: 900 } }), view: ranged });
    expect(o).toEqual({ pop: ['-12.5', '900'] });
    expect(fetchArrowTable).not.toHaveBeenCalled();
  });

  it('uses the explicit filter order and excludes unselected channels', () => {
    expect(filterableChannels({
      ...view,
      filters: [{ channel: 'day', control: 'dateRange' }],
    }).map((channel) => channel.name)).toEqual(['day']);
  });

  it('maps an explicitly selected numeric source channel into the curated list', () => {
    expect(filterableChannels({
      ...view,
      filters: [{ channel: 'pop', control: 'range' }],
    }).map((channel) => channel.name)).toEqual(['pop']);
  });
});

