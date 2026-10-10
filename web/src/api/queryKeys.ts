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
