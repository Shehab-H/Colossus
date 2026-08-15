import { queryOptions } from '@tanstack/react-query';
import { getManagedMap, listConnections, listDatasets, listManagedMaps, listSourceAdapters } from './catalogApi';

const root = ['catalog'] as const;

export const catalogQueryKeys = {
  all: root,
  mapLists: () => [...root, 'maps'] as const,
  mapList: (includeArchived: boolean) => [...root, 'maps', { includeArchived }] as const,
  mapDetails: () => [...root, 'map'] as const,
  mapDetail: (mapId: string) => [...root, 'map', mapId] as const,
  datasets: () => [...root, 'datasets'] as const,
  connections: () => [...root, 'connections'] as const,
  sourceAdapters: () => [...root, 'source-adapters'] as const,
};

export const managedMapsQuery = (includeArchived: boolean) => queryOptions({
  queryKey: catalogQueryKeys.mapList(includeArchived),
  queryFn: ({ signal }) => listManagedMaps(includeArchived, signal),
});

export const managedMapQuery = (mapId: string) => queryOptions({
  queryKey: catalogQueryKeys.mapDetail(mapId),
  queryFn: ({ signal }) => getManagedMap(mapId, signal),
  refetchInterval: (query) => query.state.data?.runs.some((run) => run.status === 'queued' || run.status === 'running')
    ? 1_500
    : false,
});

export const datasetsQuery = () => queryOptions({
  queryKey: catalogQueryKeys.datasets(),
  queryFn: ({ signal }) => listDatasets(signal),
  staleTime: 5 * 60 * 1000,
});

export const connectionsQuery = () => queryOptions({
  queryKey: catalogQueryKeys.connections(),
  queryFn: ({ signal }) => listConnections(signal),
  staleTime: 5 * 60 * 1000,
});

export const sourceAdaptersQuery = () => queryOptions({
  queryKey: catalogQueryKeys.sourceAdapters(),
  queryFn: ({ signal }) => listSourceAdapters(signal),
  staleTime: 30 * 60 * 1000,
});
