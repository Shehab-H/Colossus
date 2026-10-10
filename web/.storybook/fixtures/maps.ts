import type { ManagedMapSummary, MapDraft } from '@/types/map';
import { omitUndefined } from './omitUndefined';

export const managedMap = (overrides: Partial<ManagedMapSummary> = {}): ManagedMapSummary => omitUndefined({
  id: 'map-network-coverage',
  slug: 'network-coverage',
  title: 'Network coverage',
  sourceType: 'clickhouse',
  revision: 3,
  archived: false,
  publicationState: 'published',
  currentRelease: 'v20260814T090000000Z-1a2b3c4d-a1',
  publishedAt: '2026-08-14T09:12:00Z',
  lastRunStatus: 'succeeded',
  lastRunAt: '2026-08-14T09:12:00Z',
  outputBytes: 482_000_000,
  scheduleEnabled: true,
  nextRunAt: '2026-08-15T09:00:00Z',
  ...overrides,
});

export const publishedMap = managedMap();

export const queuedMap = managedMap({
  id: 'map-mobile-dominance',
  slug: 'mobile-dominance',
  title: 'Mobile dominance',
  sourceType: 'trino',
  revision: 1,
  publicationState: 'queued',
  currentRelease: undefined,
  publishedAt: undefined,
  lastRunStatus: 'queued',
  lastRunAt: '2026-08-14T10:02:00Z',
  outputBytes: undefined,
  scheduleEnabled: false,
  nextRunAt: undefined,
});

export const bakingMap = managedMap({
  id: 'map-place-names',
  slug: 'place-names',
  title: 'Place names',
  sourceType: 'parquet',
  publicationState: 'baking',
  lastRunStatus: 'running',
  lastRunAt: '2026-08-14T10:05:00Z',
  outputBytes: 1_320_000_000,
  scheduleEnabled: false,
  nextRunAt: undefined,
});

export const failedMap = managedMap({
  id: 'map-latency-heatmap',
  slug: 'latency-heatmap',
  title: 'Latency heatmap',
  sourceType: 'trino',
  publicationState: 'lastRunFailed',
  lastRunStatus: 'failed',
  lastRunAt: '2026-08-14T08:40:00Z',
  lastFailure: 'ClickHouse 500: Code: 241. DB::Exception: Memory limit (for query) exceeded. (MEMORY_LIMIT_EXCEEDED)',
  outputBytes: 96_400_000,
});

export const archivedMap = managedMap({
  id: 'map-legacy-roaming',
  slug: 'legacy-roaming',
  title: 'Legacy roaming',
  archived: true,
  publicationState: 'archived',
  scheduleEnabled: false,
  nextRunAt: undefined,
  outputBytes: 12_800,
});

export const neverBakedMap = managedMap({
  id: 'map-signal-quality',
  slug: 'signal-quality',
  title: 'Signal quality',
  revision: 1,
  publicationState: 'neverBaked',
  currentRelease: undefined,
  publishedAt: undefined,
  lastRunStatus: undefined,
  lastRunAt: undefined,
  outputBytes: undefined,
  scheduleEnabled: false,
  nextRunAt: undefined,
});

export const catalogMaps: ManagedMapSummary[] = [
  publishedMap,
  queuedMap,
  bakingMap,
  failedMap,
  archivedMap,
  neverBakedMap,
];

export const coverageMapDraft: MapDraft = {
  schemaVersion: 1,
  slug: 'network-coverage',
  title: 'Network coverage',
  datasetId: 'dataset-coverage',
  viewport: 'geo',
  mark: 'point',
  bakeFilters: ["country_code = 'EG'", 'tests >= 5'],
  filters: [
    { channel: 'network', control: 'select', default: '5G' },
    { channel: 'observed_on', control: 'dateRange' },
    { channel: 'signal_strength', control: 'range', default: '-100..-60' },
  ],
  measures: [
    { name: 'total_tests', expr: 'sum(tests)' },
    { name: 'dominant_network', expr: 'argmax(network, sum(tests))' },
  ],
  encoding: {
    color: { channel: 'dominant_network', type: 'categorical', scheme: 'okabeIto', overrides: { '5G': '#e60000' } },
  },
  inspect: { channels: ['signal_strength', 'network', 'total_tests'], title: 'network' },
};
