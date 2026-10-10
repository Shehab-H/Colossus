import type { BadgeTone } from '@/components/ui';
import type { BakeRunCounts, BakeRunStatus, ManagedBakeRun } from '@/types/bake';

export type RunFilter = 'all' | 'active' | 'failed' | 'succeeded' | 'cancelled';

export const runFilters: ReadonlyArray<{ id: RunFilter; label: string; statuses: readonly BakeRunStatus[] }> = [
  { id: 'all', label: 'All', statuses: [] },
  { id: 'active', label: 'Active', statuses: ['queued', 'running'] },
  { id: 'failed', label: 'Failed', statuses: ['failed'] },
  { id: 'succeeded', label: 'Succeeded', statuses: ['succeeded'] },
  { id: 'cancelled', label: 'Cancelled', statuses: ['cancelled'] },
];

export function runFilterStatuses(filter: RunFilter): readonly BakeRunStatus[] {
  return runFilters.find((value) => value.id === filter)?.statuses ?? [];
}

export function runFilterCount(filter: RunFilter, counts: BakeRunCounts): number {
  const statuses = runFilterStatuses(filter);
  const included = statuses.length === 0 ? (Object.keys(counts) as BakeRunStatus[]) : statuses;
  return included.reduce((total, status) => total + (counts[status] ?? 0), 0);
}

export function isActiveRun(run: ManagedBakeRun): boolean {
  return run.status === 'queued' || run.status === 'running';
}

export function runStatusTone(status: BakeRunStatus): BadgeTone {
  switch (status) {
    case 'failed': return 'danger';
    case 'succeeded': return 'success';
    case 'cancelled': return 'neutral';
    default: return 'info';
  }
}

export function runOutcome(run: ManagedBakeRun): string | undefined {
  return run.failureCategory ?? run.statusMessage ?? run.error;
}

export function runFailureDetails(run: ManagedBakeRun): string | undefined {
  return run.failureDetails ?? (run.status === 'failed' ? run.error : undefined);
}
