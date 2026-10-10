import { useMutation, useQueryClient } from '@tanstack/react-query';
import { archiveMap, createMap, updateMap } from '@/api/maps';
import { cancelMapRun, runMapNow, pinMapRelease, reconcileMapReleases, setBakePolicy } from '@/api/bakes';
import { catalogQueryKeys } from '@/api/queryKeys';
import type { MapDraft, SavedMap } from '@/types/map';
import type { ManagedBakeRun, SaveBakePolicy } from '@/types/bake';

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

export function usePinMapReleaseMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mapId, releaseId, pinned }: { mapId: string; releaseId: string; pinned: boolean }) =>
      pinMapRelease(mapId, releaseId, pinned),
    onSuccess: (_, { mapId }) => invalidateMapState(queryClient, mapId),
  });
}

export function useReconcileMapReleasesMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (mapId: string) => reconcileMapReleases(mapId),
    onSuccess: (_, mapId) => invalidateMapState(queryClient, mapId),
  });
}

export interface SaveManagedMapInput {
  mapId?: string;
  expectedRevision?: number;
  definition: MapDraft;
  policy: SaveBakePolicy;
  policyRevision?: number;
  definitionChanged: boolean;
  queueBake?: boolean;
}

export interface SaveManagedMapResult {
  saved: SavedMap;
  run?: ManagedBakeRun;
  definitionChanged: boolean;
}

export class ManagedMapPartialSaveError extends Error {
  readonly saved: SavedMap;
  readonly run: ManagedBakeRun | undefined;
  readonly phase: 'queue' | 'policy';

  constructor(
    message: string,
    saved: SavedMap,
    run: ManagedBakeRun | undefined,
    phase: 'queue' | 'policy',
    options?: ErrorOptions,
  ) {
    super(message, options);
    this.name = 'ManagedMapPartialSaveError';
    this.saved = saved;
    this.run = run;
    this.phase = phase;
  }
}

export function useSaveManagedMapMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ mapId, expectedRevision, definition, policy, policyRevision, definitionChanged, queueBake = definitionChanged }: SaveManagedMapInput): Promise<SaveManagedMapResult> => {
      if (mapId && expectedRevision === undefined) throw new Error('An expected map revision is required when editing.');
      const saved = definitionChanged
        ? mapId ? await updateMap(mapId, expectedRevision!, definition) : await createMap(definition)
        : { id: mapId!, revision: expectedRevision!, draft: definition };
      let run: ManagedBakeRun | undefined;
      if (queueBake) {
        try { run = await runMapNow(saved.id); }
        catch (reason) {
          throw new ManagedMapPartialSaveError(
            `Configuration revision ${saved.revision} was saved, but its bake could not be queued.`,
            saved,
            undefined,
            'queue',
            { cause: reason },
          );
        }
      }
      try { await setBakePolicy(saved.id, policy, policyRevision); }
      catch (reason) {
        throw new ManagedMapPartialSaveError(
          run ? `Bake ${run.id} was queued, but the schedule and retention policy was not saved.` : 'The map policy was not saved.',
          saved,
          run,
          'policy',
          { cause: reason },
        );
      }
      return { saved, run, definitionChanged };
    },
    onSuccess: (result) => invalidateMapState(queryClient, result.saved.id),
  });
}
