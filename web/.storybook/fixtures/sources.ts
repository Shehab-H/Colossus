import type {
  CatalogConnection,
  CatalogDataset,
  DatasetChannelSpec,
  SourceAdapterDescriptor,
  SourceConnectionValidation,
  SourceLocation,
  SourcePreview,
  SourceSchema,
} from '@/types/source';

export const sourceAdapters: SourceAdapterDescriptor[] = [
  {
    id: 'clickhouse',
    name: 'ClickHouse',
    capabilities: {
      connectionValidation: true,
      locationDiscovery: true,
      schemaInspection: true,
      boundedPreview: true,
      canonicalExtraction: true,
    },
    configurationFields: [
      { key: 'host', label: 'Host', required: true, secret: false, placeholder: 'clickhouse.internal' },
      { key: 'port', label: 'HTTP port', required: true, secret: false, placeholder: '8123' },
      { key: 'database', label: 'Database', required: true, secret: false, placeholder: 'default' },
      { key: 'protocol', label: 'Protocol', required: true, secret: false, allowedValues: ['http', 'https'] },
      { key: 'username', label: 'Username', required: true, secret: false },
      { key: 'password', label: 'Password', required: false, secret: true },
    ],
    geometryKinds: ['lonLat', 'xy', 'quadkey'],
    datasetInputLabel: 'Source query',
    datasetInputDescription: 'A bounded SELECT statement over one or more tables.',
    datasetInputPlaceholder: 'SELECT lon, lat, signal_strength FROM coverage',
  },
  {
    id: 'parquet',
    name: 'Parquet files',
    capabilities: {
      connectionValidation: true,
      locationDiscovery: true,
      schemaInspection: true,
      boundedPreview: true,
      canonicalExtraction: true,
    },
    configurationFields: [
      { key: 'root', label: 'Root path', required: true, secret: false, placeholder: '/data/parquet' },
    ],
    geometryKinds: ['lonLat', 'xy', 'quadkey', 'wkt'],
    datasetInputLabel: 'File glob',
    datasetInputDescription: 'A path or glob relative to the connection root.',
    datasetInputPlaceholder: 'coverage/*.parquet',
  },
];

export const connections: CatalogConnection[] = [
  {
    id: 'connection-warehouse',
    workspaceKey: 'default',
    slug: 'primary-warehouse',
    name: 'Primary warehouse',
    adapter: 'clickhouse',
    revision: 2,
    archived: false,
    createdAt: '2026-07-02T08:30:00Z',
    updatedAt: '2026-07-20T14:05:00Z',
  },
  {
    id: 'connection-files',
    workspaceKey: 'default',
    slug: 'coverage-files',
    name: 'Coverage files',
    adapter: 'parquet',
    revision: 1,
    archived: false,
    createdAt: '2026-07-05T10:00:00Z',
    updatedAt: '2026-07-05T10:00:00Z',
  },
];

export const connectionValidation: SourceConnectionValidation = {
  message: 'Connection verified.',
  details: { server: '24.8.2', database: 'default', latency: '38 ms' },
};

export const sourceLocations: SourceLocation[] = [
  { namespace: 'default', name: 'coverage', kind: 'table' },
  { namespace: 'default', name: 'coverage_hourly', kind: 'view' },
  { namespace: 'default', name: 'places', kind: 'table' },
];

export const coverageChannels: DatasetChannelSpec[] = [
  { name: 'signal_strength', column: 'signal_strength', role: 'measure', type: 'f32' },
  { name: 'tests', column: 'tests', role: 'measure', type: 'u16' },
  { name: 'network', column: 'network', role: 'dimension', type: 'dict' },
  { name: 'country_code', column: 'country_code', role: 'dimension', type: 'dict' },
  { name: 'observed_on', column: 'observed_on', role: 'temporal', type: 'date' },
];

export const coverageDataset: CatalogDataset = {
  id: 'dataset-coverage',
  workspaceKey: 'default',
  draft: {
    slug: 'mobile-coverage',
    name: 'Mobile coverage',
    connectionId: 'connection-warehouse',
    source: {
      query: 'SELECT lon, lat, signal_strength, tests, network, country_code, observed_on FROM coverage',
      geometry: { kind: 'lonLat', lon: 'lon', lat: 'lat' },
      channels: coverageChannels,
    },
  },
  revision: 1,
  archived: false,
  createdAt: '2026-07-06T09:00:00Z',
  updatedAt: '2026-07-06T09:00:00Z',
};

export const placesDataset: CatalogDataset = {
  id: 'dataset-places',
  workspaceKey: 'default',
  draft: {
    slug: 'place-names',
    name: 'Place names',
    connectionId: 'connection-files',
    source: {
      query: 'places/*.parquet',
      geometry: { kind: 'wkt', column: 'geometry_wkt', geographic: true },
      channels: [
        { name: 'feature_class', column: 'feature_class', role: 'dimension', type: 'dict' },
        { name: 'population', column: 'population', role: 'measure', type: 'i64' },
      ],
    },
  },
  revision: 2,
  archived: false,
  createdAt: '2026-07-08T11:15:00Z',
  updatedAt: '2026-07-22T16:45:00Z',
};

export const datasets: CatalogDataset[] = [coverageDataset, placesDataset];

export const coverageSchema: SourceSchema = {
  columns: [
    { name: 'lon', sourceType: 'Float64', nullable: false },
    { name: 'lat', sourceType: 'Float64', nullable: false },
    { name: 'signal_strength', sourceType: 'Float32', nullable: true },
    { name: 'tests', sourceType: 'UInt16', nullable: false },
    { name: 'network', sourceType: 'String', nullable: false },
    { name: 'country_code', sourceType: 'String', nullable: false },
    { name: 'observed_on', sourceType: 'Date', nullable: false },
    { name: 'footprint', sourceType: 'Polygon', nullable: true },
  ],
  warnings: ['Column footprint has an unsupported type and cannot be used as a channel.'],
};

export const coveragePreview: SourcePreview = {
  columns: [
    { name: 'lon', sourceType: 'Float64', nullable: false, role: 'geometry' },
    { name: 'lat', sourceType: 'Float64', nullable: false, role: 'geometry' },
    { name: 'signal_strength', sourceType: 'Float32', nullable: true, role: 'measure' },
    { name: 'network', sourceType: 'String', nullable: false, role: 'dimension' },
  ],
  rows: [
    { lon: '31.2357', lat: '30.0444', signal_strength: '-63', network: '5G' },
    { lon: '13.405', lat: '52.52', signal_strength: '-71', network: '4G' },
    { lon: '-0.1276', lat: '51.5072', signal_strength: null, network: '4G' },
  ],
  geometry: {
    kind: 'lonLat',
    geographic: true,
    bounds: { minX: -0.1276, minY: 30.0444, maxX: 31.2357, maxY: 52.52 },
    sampledRows: 3,
    distinctGeometries: 3,
  },
  truncated: false,
  resultBytes: 612,
  elapsed: '00:00:00.4210000',
};
