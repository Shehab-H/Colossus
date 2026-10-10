import { queryOptions } from '@tanstack/react-query';
import type { CatalogDataset, CatalogConnection, SourceAdapterDescriptor, CreateConnectionInput, CreateDatasetInput, SourceConnectionValidation, SourceLocation, SourceSchema, SourcePreview } from '@/types/source';
import { request } from './client';
import { catalogQueryKeys } from './queryKeys';

export const listDatasets = (signal?: AbortSignal): Promise<CatalogDataset[]> => request('/datasets', { signal });

export const listConnections = (signal?: AbortSignal): Promise<CatalogConnection[]> =>
  request('/connections', { signal });

export const listSourceAdapters = (signal?: AbortSignal): Promise<SourceAdapterDescriptor[]> =>
  request('/source-adapters', { signal });

export const createConnection = (input: CreateConnectionInput): Promise<CatalogConnection> =>
  request('/connections', { method: 'POST', body: JSON.stringify(input) });

export const createDataset = (input: CreateDatasetInput): Promise<CatalogDataset> =>
  request('/datasets', { method: 'POST', body: JSON.stringify(input) });

export const testConnection = (connectionId: string): Promise<SourceConnectionValidation> =>
  request(`/connections/${connectionId}/test`, { method: 'POST' });

export const discoverSourceLocations = (connectionId: string): Promise<SourceLocation[]> =>
  request(`/connections/${connectionId}/locations`);

export const inspectSourceSchema = (input: { connectionId: string; query: string }): Promise<SourceSchema> =>
  request(`/connections/${input.connectionId}/schema`, {
    method: 'POST',
    body: JSON.stringify({ query: input.query }),
  });

export const previewDataset = (datasetId: string): Promise<SourcePreview> =>
  request(`/datasets/${datasetId}/preview`, { method: 'POST' });
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
