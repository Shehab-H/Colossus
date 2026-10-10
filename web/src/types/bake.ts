export type BakeRunStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
export type BakeRunStage = 'queued' | 'probing' | 'extracting' | 'tiling' | 'verifying' | 'publishing' | 'cleanup' | 'succeeded' | 'failed' | 'cancelled';

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

export interface SaveBakePolicy {
  enabled: boolean;
  interval?: string;
  timeZoneId: string;
  allowOverlap: boolean;
  retainSuccessfulReleases: number;
  retainSuccessfulFor?: string;
}
