import { queryOptions } from '@tanstack/react-query';
import type { ManagedMapSummary, ManagedMapDetail, MapDraft, SavedMap } from '@/types/map';
import { ACTIVE_RUN_POLL_MS, hasActiveRuns } from './bakes';
import { request } from './client';
import { catalogQueryKeys } from './queryKeys';

export const listManagedMaps = (includeArchived = false, signal?: AbortSignal): Promise<ManagedMapSummary[]> =>
  request(`/maps?includeArchived=${includeArchived}`, { signal });

export const getManagedMap = (id: string, signal?: AbortSignal): Promise<ManagedMapDetail> =>
  request(`/maps/${id}`, { signal });

export const createMap = (definition: MapDraft): Promise<SavedMap> =>
  request('/maps', { method: 'POST', body: JSON.stringify(definition) });

export const updateMap = (id: string, expectedRevision: number, definition: MapDraft): Promise<SavedMap> =>
  request(`/maps/${id}?expectedRevision=${expectedRevision}`, { method: 'PUT', body: JSON.stringify(definition) });

export const archiveMap = (id: string, expectedRevision: number): Promise<SavedMap> =>
  request(`/maps/${id}?expectedRevision=${expectedRevision}`, { method: 'DELETE' });
export const managedMapsQuery = (includeArchived: boolean) => queryOptions({
  queryKey: catalogQueryKeys.mapList(includeArchived),
  queryFn: ({ signal }) => listManagedMaps(includeArchived, signal),
});

export const managedMapQuery = (mapId: string) => queryOptions({
  queryKey: catalogQueryKeys.mapDetail(mapId),
  queryFn: ({ signal }) => getManagedMap(mapId, signal),
  refetchInterval: (query) => hasActiveRuns(query.state.data?.runCounts) ? ACTIVE_RUN_POLL_MS : false,
});
