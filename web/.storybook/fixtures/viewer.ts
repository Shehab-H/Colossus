import type { Legend } from '@/lib/color/legend';
import type { ChannelSpec, FilterSpec } from '@/types/viewConfig';

export const viewerChannels: ChannelSpec[] = [
  { name: 'feature_class', column: 'feature_class', role: 'dimension', type: 'dict' },
  { name: 'country_code', column: 'country_code', role: 'dimension', type: 'dict' },
  { name: 'modification_date', column: 'modification_date', role: 'temporal', type: 'date' },
];

export const viewerControls: Record<string, FilterSpec['control']> = {
  feature_class: 'select',
  country_code: 'select',
  modification_date: 'dateRange',
};

export const viewerOptions: Record<string, string[]> = {
  feature_class: ['A', 'H', 'L', 'P', 'S'],
  country_code: ['DE', 'EG', 'GB', 'US'],
  modification_date: ['2026-01-01', '2026-08-14'],
};

export const populationChannel: ChannelSpec = { name: 'population', column: 'population', role: 'measure', type: 'i64' };

export const gradientLegend: Legend = {
  channel: 'signal_strength',
  note: 'gradient',
  kind: 'continuous',
  gradient: [[255, 239, 160], [230, 0, 0], [105, 0, 45]],
  min: -120,
  midpoint: -80,
  max: -40,
};

export const clampedLegend: Legend = {
  ...gradientLegend,
  note: 'log scale',
  minClamped: true,
  maxClamped: true,
};

export const binnedLegend: Legend = {
  channel: 'tests',
  note: 'binned',
  kind: 'binned',
  items: [
    { color: [254, 229, 217], label: '< 100' },
    { color: [252, 174, 145], label: '100 – 1.0k' },
    { color: [251, 106, 74], label: '1.0k – 10.0k' },
    { color: [203, 24, 29], label: '≥ 10.0k' },
  ],
};

export const categoricalLegend: Legend = {
  channel: 'network',
  note: 'categorical',
  kind: 'categorical',
  items: [
    { color: [230, 0, 0], label: '5G' },
    { color: [0, 103, 177], label: '4G' },
    { color: [24, 134, 75], label: '3G' },
    { color: [168, 93, 0], label: '2G' },
  ],
  more: 3,
};
