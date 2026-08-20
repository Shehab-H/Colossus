export type PublicationState =
  | 'neverBaked'
  | 'queued'
  | 'baking'
  | 'published'
  | 'lastRunFailed'
  | 'archived';

export type BakeRunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
export type BakeRunStage = 'queued' | 'probing' | 'extracting' | 'tiling' | 'verifying' | 'publishing' | 'cleanup' | 'succeeded' | 'failed' | 'cancelled';

export interface GeometrySpec {
  kind: string;
  x?: string;
  y?: string;
  lon?: string;
  lat?: string;
  column?: string;
  geographic?: boolean;
}

export interface ChannelSpec {
  name: string;
  column: string;
  role: string;
  type: string;
}

export interface DatasetSource {
  query: string;
  geometry: GeometrySpec;
  channels: ChannelSpec[];
}

export interface CatalogDataset {
  id: string;
  workspaceKey: string;
  draft: {
    slug: string;
    name: string;
    connectionId: string;
    source: DatasetSource;
  };
  revision: number;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CatalogConnection {
  id: string;
  workspaceKey: string;
  slug: string;
  name: string;
  adapter: string;
  revision: number;
  archived: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface SourceConfigurationField {
  key: string;
  label: string;
  required: boolean;
  secret: boolean;
  placeholder?: string;
  allowedValues?: string[];
}

export interface SourceAdapterDescriptor {
  id: string;
  name: string;
  capabilities: {
    connectionValidation: boolean;
    locationDiscovery: boolean;
    schemaInspection: boolean;
    boundedPreview: boolean;
    canonicalExtraction: boolean;
  };
  configurationFields: SourceConfigurationField[];
  geometryKinds: string[];
  datasetInputLabel: string;
  datasetInputDescription: string;
  datasetInputPlaceholder: string;
}

export interface SourceConnectionValidation {
  message: string;
  details: Record<string, string>;
}

export interface SourceLocation {
  namespace: string;
  name: string;
  kind: string;
}

export interface SourceSchemaColumn {
  name: string;
  sourceType: string;
  nullable: boolean;
}

export interface SourceSchema {
  columns: SourceSchemaColumn[];
  warnings?: string[];
}

export interface SourcePreviewColumn {
  name: string;
  sourceType: string;
  nullable: boolean;
  role: string;
}

export interface SourcePreview {
  columns: SourcePreviewColumn[];
  rows: Record<string, string | null>[];
  geometry: {
    kind: string;
    geographic: boolean;
    bounds: { minX: number; minY: number; maxX: number; maxY: number };
    sampledRows: number;
    distinctGeometries: number;
  };
  truncated: boolean;
  resultBytes: number;
  elapsed: string;
  warnings?: string[];
}

export interface CreateConnectionInput {
  definition: {
    slug: string;
    name: string;
    adapter: string;
    settings: Record<string, string>;
  };
  secrets: Record<string, string>;
}

export interface CreateDatasetInput {
  slug: string;
  name: string;
  connectionId: string;
  source: DatasetSource;
}

export interface MapDraft {
  schemaVersion: number;
  slug: string;
  title?: string;
  datasetId: string;
  viewport: 'geo' | 'orthographic';
  mark: 'point' | 'line' | 'arc' | 'rect' | 'polygon' | 'heat' | 'text';
  reduction?: string;
  bakeFilters?: string[];
  filters?: unknown[];
  measures?: unknown[];
  aggregate?: unknown;
  storage?: unknown;
  encoding?: unknown;
  inspect?: unknown;
}

export interface ManagedMapSummary {
  id: string;
  slug: string;
  title: string;
  sourceType: string;
  revision: number;
  archived: boolean;
  publicationState: PublicationState;
  currentRelease?: string;
  publishedAt?: string;
  lastRunStatus?: BakeRunStatus;
  lastRunAt?: string;
  lastFailure?: string;
  outputBytes?: number;
  scheduleEnabled: boolean;
  nextRunAt?: string;
}

export interface BakePolicy {
  mapId: string;
  enabled: boolean;
  interval?: string;
  timeZoneId: string;
  allowOverlap: boolean;
  retainSuccessfulReleases: number;
  revision: number;
  updatedAt: string;
  retainSuccessfulFor?: string;
}

export interface ManagedBakeRun {
  id: string;
  draftRevision: number;
  status: BakeRunStatus;
  stage: BakeRunStage;
  progress: number;
  statusMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
  failureCategory?: string;
  failureDetails?: string;
  sourceRows?: number;
  tileCount?: number;
  artifactBytes?: number;
  releaseId?: string;
  cancellationRequested: boolean;
  attempt: number;
}

export interface ManagedRelease {
  id: string;
  bakeRunId: string;
  version: string;
  artifactLocation: string;
  publishedAt: string;
  outputBytes?: number;
  pinned: boolean;
  manifestHash?: string;
  sourceRows?: number;
  tileCount?: number;
  verificationPassed: boolean;
  verifiedAt?: string;
}

export interface RetentionDecision {
  kind: 'release' | 'workspace';
  identity: string;
  action: 'retained' | 'deleted' | 'skipped';
  reason: string;
}

export interface ReleaseMaintenanceReport {
  decisions: RetentionDecision[];
  deleted: number;
}

export interface ManagedMapDetail {
  summary: ManagedMapSummary;
  definition: MapDraft;
  dataset: {
    id: string;
    slug: string;
    name: string;
    connectionId: string;
    sourceType: string;
    source: DatasetSource;
  };
  bakePolicy?: BakePolicy;
  runs: ManagedBakeRun[];
  releases: ManagedRelease[];
}

export interface SaveBakePolicy {
  enabled: boolean;
  interval?: string;
  timeZoneId: string;
  allowOverlap: boolean;
  retainSuccessfulReleases: number;
  retainSuccessfulFor?: string;
}

export interface SavedMap {
  id: string;
  revision: number;
  draft: MapDraft;
}

export interface ApiValidationProblem {
  title?: string;
  error?: string;
  errors?: Record<string, string[]>;
  sourceErrorKind?: string;
  transient?: boolean;
}
