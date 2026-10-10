import { useMutation } from '@tanstack/react-query';
import { queryClient } from '@/api/queryClient';
import { catalogQueryKeys } from '@/api/queryKeys';
import { createConnection, createDataset, discoverSourceLocations, inspectSourceSchema, previewDataset, testConnection } from '@/api/sources';

export function useCreateConnectionMutation() {
  return useMutation({
    mutationFn: createConnection,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: catalogQueryKeys.connections() }),
  });
}

export function useCreateDatasetMutation() {
  return useMutation({
    mutationFn: createDataset,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: catalogQueryKeys.datasets() }),
  });
}

export function useTestConnectionMutation() {
  return useMutation({ mutationFn: testConnection });
}

export function useDiscoverLocationsMutation() {
  return useMutation({ mutationFn: discoverSourceLocations });
}

export function useInspectSourceSchemaMutation() {
  return useMutation({ mutationFn: inspectSourceSchema });
}

export function usePreviewDatasetMutation() {
  return useMutation({ mutationFn: previewDataset });
}
