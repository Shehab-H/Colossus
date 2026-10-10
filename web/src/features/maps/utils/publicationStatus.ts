import type { PublicationState } from '@/types/map';
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
