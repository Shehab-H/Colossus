export type PublicationState =
  | 'neverBaked'
  | 'queued'
  | 'baking'
  | 'published'
  | 'lastRunFailed'
  | 'archived';

export type BakeRunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';

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
}

export interface ManagedBakeRun {
  id: string;
  draftRevision: number;
  status: BakeRunStatus;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  error?: string;
}

export interface ManagedRelease {
  id: string;
  bakeRunId: string;
  version: string;
  artifactLocation: string;
  publishedAt: string;
  outputBytes?: number;
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
}
