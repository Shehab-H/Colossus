import { infiniteQueryOptions } from '@tanstack/react-query';
import type { BakePolicy, BakeRunCounts, BakeRunPage, BakeRunStatus, ManagedBakeRun, ManagedRelease, ReleaseMaintenanceReport, SaveBakePolicy } from '@/types/bake';
import { request } from './client';
import { catalogQueryKeys } from './queryKeys';

export const ACTIVE_RUN_POLL_MS = 1_500;
const RUN_PAGE_SIZE = 25;

export const hasActiveRuns = (counts?: BakeRunCounts): boolean =>
  Boolean(counts && counts.queued + counts.running > 0);

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

export function listMapRuns(
  mapId: string,
  statuses: readonly BakeRunStatus[],
  cursor?: string,
  signal?: AbortSignal,
): Promise<BakeRunPage> {
  const params = new URLSearchParams({ limit: String(RUN_PAGE_SIZE) });
  for (const status of statuses) params.append('status', status);
  if (cursor) params.set('cursor', cursor);
  return request(`/maps/${mapId}/runs?${params}`, { signal });
}

export const mapRunsQuery = (mapId: string, statuses: readonly BakeRunStatus[]) => infiniteQueryOptions({
  queryKey: catalogQueryKeys.mapRuns(mapId, statuses),
  queryFn: ({ pageParam, signal }) => listMapRuns(mapId, statuses, pageParam, signal),
  initialPageParam: undefined as string | undefined,
  getNextPageParam: (page) => page.nextCursor,
});

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
