import { useMutation } from '@tanstack/react-query';
import {
  catalogQueryKeys,
  createConnection,
  createDataset,
  discoverSourceLocations,
  previewDataset,
  testConnection,
} from '@entities/catalog';
import { queryClient } from '@shared/query/queryClient';

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

export function usePreviewDatasetMutation() {
  return useMutation({ mutationFn: previewDataset });
}
