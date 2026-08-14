import type {
  ApiValidationProblem,
  BakePolicy,
  CatalogDataset,
  ManagedBakeRun,
  ManagedMapDetail,
  ManagedMapSummary,
  MapDraft,
  SaveBakePolicy,
  SavedMap,
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

export const listManagedMaps = (includeArchived = false): Promise<ManagedMapSummary[]> =>
  request(`/maps?includeArchived=${includeArchived}`);

export const getManagedMap = (id: string): Promise<ManagedMapDetail> => request(`/maps/${id}`);

export const listDatasets = (): Promise<CatalogDataset[]> => request('/datasets');

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
