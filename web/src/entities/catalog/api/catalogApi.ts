import type {
  ApiValidationProblem,
  BakePolicy,
  CatalogConnection,
  CatalogDataset,
  CreateConnectionInput,
  CreateDatasetInput,
  ManagedBakeRun,
  ManagedMapDetail,
  ManagedMapSummary,
  MapDraft,
  SaveBakePolicy,
  SavedMap,
  SourceAdapterDescriptor,
  SourceConnectionValidation,
  SourceLocation,
  SourcePreview,
  SourceSchema,
} from '../model/catalog';
import { API_BASE } from '@shared/api/config';

export class CatalogApiError extends Error {
  readonly status: number;
  readonly fieldErrors: Record<string, string[]>;

  constructor(status: number, problem: ApiValidationProblem) {
    super(problem.title ?? problem.error ?? `Catalog request failed (${status})`);
    this.name = 'CatalogApiError';
    this.status = status;
    this.fieldErrors = problem.errors ?? {};
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  });
  if (!response.ok) {
    let problem: ApiValidationProblem = {};
    try { problem = await response.json(); } catch { /* non-JSON infrastructure failure */ }
    throw new CatalogApiError(response.status, problem);
  }
  return response.json();
}

export const listManagedMaps = (includeArchived = false, signal?: AbortSignal): Promise<ManagedMapSummary[]> =>
  request(`/maps?includeArchived=${includeArchived}`, { signal });

export const getManagedMap = (id: string, signal?: AbortSignal): Promise<ManagedMapDetail> =>
  request(`/maps/${id}`, { signal });

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

export const createMap = (definition: MapDraft): Promise<SavedMap> =>
  request('/maps', { method: 'POST', body: JSON.stringify(definition) });

export const updateMap = (id: string, expectedRevision: number, definition: MapDraft): Promise<SavedMap> =>
  request(`/maps/${id}?expectedRevision=${expectedRevision}`, { method: 'PUT', body: JSON.stringify(definition) });

export const archiveMap = (id: string, expectedRevision: number): Promise<SavedMap> =>
  request(`/maps/${id}?expectedRevision=${expectedRevision}`, { method: 'DELETE' });

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
