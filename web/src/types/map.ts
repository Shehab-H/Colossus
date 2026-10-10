import type { BakeRunCounts, BakeRunStatus, BakePolicy, ManagedRelease } from './bake';
import type { DatasetSource } from './source';

export type PublicationState =
  | 'neverBaked'
  | 'queued'
  | 'baking'
  | 'published'
  | 'lastRunFailed'
  | 'archived';

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
  runCounts: BakeRunCounts;
  releases: ManagedRelease[];
}

export interface SavedMap {
  id: string;
  revision: number;
  draft: MapDraft;
}
