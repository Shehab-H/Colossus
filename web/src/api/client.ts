export const TILES_BASE = import.meta.env.VITE_TILES_BASE ?? 'http://localhost:5174/tiles';
export const API_BASE = import.meta.env.VITE_API_BASE ?? TILES_BASE.replace(/\/tiles\/?$/, '/api');

export interface ApiValidationProblem {
  title?: string;
  error?: string;
  errors?: Record<string, string[]>;
  sourceErrorKind?: string;
  transient?: boolean;
}

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

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
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
