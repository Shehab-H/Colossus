import type { SaveBakePolicy, BakePolicy, ManagedBakeRun, ManagedRelease, ReleaseMaintenanceReport } from '@/types/bake';
import { request } from './client';

export const setBakePolicy = (
  id: string,
  policy: SaveBakePolicy,
  expectedRevision?: number,
): Promise<BakePolicy> => request(
  `/maps/${id}/bake-policy${expectedRevision === undefined ? '' : `?expectedRevision=${expectedRevision}`}`,
  { method: 'PUT', body: JSON.stringify(policy) },
);

export const runMapNow = (id: string): Promise<ManagedBakeRun> =>
  request(`/maps/${id}/runs`, { method: 'POST' });

export const cancelMapRun = (mapId: string, runId: string): Promise<ManagedBakeRun> =>
  request(`/maps/${mapId}/runs/${runId}/cancel`, { method: 'POST' });

export const pinMapRelease = (
  mapId: string,
  releaseId: string,
  pinned: boolean,
): Promise<ManagedRelease> => request(
  `/maps/${mapId}/releases/${releaseId}/pin?pinned=${pinned}`,
  { method: 'PUT' },
);

export const reconcileMapReleases = (mapId: string): Promise<ReleaseMaintenanceReport> =>
  request(`/maps/${mapId}/releases/reconcile`, { method: 'POST' });
