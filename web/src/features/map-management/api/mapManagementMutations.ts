import { useMutation, useQueryClient } from '@tanstack/react-query';
import {
  archiveMap,
  cancelMapRun,
  catalogQueryKeys,
  createMap,
  runMapNow,
  setBakePolicy,
  updateMap,
  type MapDraft,
  type SaveBakePolicy,
} from '@entities/catalog';

async function invalidateMapState(queryClient: ReturnType<typeof useQueryClient>, mapId?: string) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: catalogQueryKeys.mapLists() }),
    ...(mapId ? [queryClient.invalidateQueries({ queryKey: catalogQueryKeys.mapDetail(mapId) })] : []),
  ]);
}

export function useArchiveMapMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mapId, expectedRevision }: { mapId: string; expectedRevision: number }) =>
      archiveMap(mapId, expectedRevision),
    onSuccess: (_, { mapId }) => invalidateMapState(queryClient, mapId),
  });
}

export function useRunMapMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (mapId: string) => runMapNow(mapId),
    onSuccess: (_, mapId) => invalidateMapState(queryClient, mapId),
  });
}

export function useCancelMapRunMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mapId, runId }: { mapId: string; runId: string }) => cancelMapRun(mapId, runId),
    onSuccess: (_, { mapId }) => invalidateMapState(queryClient, mapId),
  });
}

export function useBakePolicyMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mapId, policy, expectedRevision }: {
      mapId: string;
      policy: SaveBakePolicy;
      expectedRevision?: number;
    }) => setBakePolicy(mapId, policy, expectedRevision),
    onSuccess: (_, { mapId }) => invalidateMapState(queryClient, mapId),
  });
}

export interface SaveManagedMapInput {
  mapId?: string;
  expectedRevision?: number;
  definition: MapDraft;
  policy: SaveBakePolicy;
  policyRevision?: number;
}

export function useSaveManagedMapMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ mapId, expectedRevision, definition, policy, policyRevision }: SaveManagedMapInput) => {
      if (mapId && expectedRevision === undefined) throw new Error('An expected map revision is required when editing.');
      const saved = mapId
        ? await updateMap(mapId, expectedRevision!, definition)
        : await createMap(definition);
      await setBakePolicy(saved.id, policy, policyRevision);
      return saved;
    },
    onSuccess: (saved) => invalidateMapState(queryClient, saved.id),
  });
}
