import type { PublicationState } from '@entities/catalog';

export type CatalogContentState = 'loading' | 'error' | 'empty' | 'ready';

export function catalogContentState(itemCount: number | null, hasError: boolean): CatalogContentState {
  if (itemCount === null) return 'loading';
  if (hasError) return 'error';
  return itemCount === 0 ? 'empty' : 'ready';
}

export const publicationLabel = (state: PublicationState): string => ({
  neverBaked: 'Never baked',
  queued: 'Queued',
  baking: 'Baking',
  published: 'Published',
  lastRunFailed: 'Last run failed',
  archived: 'Archived',
})[state];

export const publicationTone = (state: PublicationState): 'neutral' | 'progress' | 'success' | 'danger' => ({
  neverBaked: 'neutral' as const,
  queued: 'progress' as const,
  baking: 'progress' as const,
  published: 'success' as const,
  lastRunFailed: 'danger' as const,
  archived: 'neutral' as const,
})[state];

export function formatDate(value?: string): string {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

export function formatBytes(value?: number): string {
  if (value === undefined) return 'Pending metadata';
  if (value >= 1_000_000_000) return `${(value / 1_000_000_000).toFixed(1)} GB`;
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)} MB`;
  if (value >= 1_000) return `${(value / 1_000).toFixed(1)} KB`;
  return `${value} B`;
}

export const viewerHref = (slug: string): string => `/maps/${encodeURIComponent(slug)}`;
export const embedHref = (slug: string): string => `/embed/${encodeURIComponent(slug)}`;
