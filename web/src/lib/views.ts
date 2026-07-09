import { API_BASE } from './manifest';

/** One row of GET /api/views — the registry listing that drives the view picker. */
export interface ViewSummary {
  id: string;
  title?: string;
  viewport: string;
  mark: string;
  url: string;
  baked: boolean;
}

export async function listViews(): Promise<ViewSummary[]> {
  const res = await fetch(`${API_BASE}/views`);
  if (!res.ok) throw new Error(`views API ${res.status}`);
  return res.json();
}

/** The ?view= deep link that /api/views/{id}/url hands out. */
export const urlViewId = (): string | null => new URLSearchParams(window.location.search).get('view');

export function setUrlViewId(id: string): void {
  const url = new URL(window.location.href);
  url.searchParams.set('view', id);
  window.history.replaceState(null, '', url);
}
