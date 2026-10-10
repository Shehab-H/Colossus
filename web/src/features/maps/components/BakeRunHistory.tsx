import { useState } from 'react';
import { hasActiveRuns } from '@/api/bakes';
import { Alert, Button, LoadingState } from '@/components/ui';
import type { BakeRunCounts } from '@/types/bake';
import { useCancelMapRunMutation } from '../hooks/mapMutations';
import { useMapRuns } from '../hooks/useMapRuns';
import { runFilterCount, runFilterStatuses, type RunFilter } from '../utils/bakeRunStatus';
import BakeRunFilterBar from './BakeRunFilterBar';
import BakeRunTable from './BakeRunTable';

export default function BakeRunHistory({ mapId, counts }: { mapId: string; counts: BakeRunCounts }) {
  const [filter, setFilter] = useState<RunFilter>('all');
  const runsQuery = useMapRuns(mapId, runFilterStatuses(filter), hasActiveRuns(counts));
  const cancelMutation = useCancelMapRunMutation();
  const error = runsQuery.error ?? cancelMutation.error;
  const total = runFilterCount(filter, counts);

  return (
    <div className="run-history">
      <BakeRunFilterBar value={filter} counts={counts} onChange={setFilter} />
      {error && <Alert tone="danger">{error.message}</Alert>}
      {runsQuery.isPending
        ? <LoadingState>Loading runs…</LoadingState>
        : runsQuery.runs.length === 0
          ? <p className="run-history__empty">{filter === 'all' ? 'This map has never been queued.' : 'No runs match this filter.'}</p>
          : <BakeRunTable runs={runsQuery.runs} cancelling={cancelMutation.isPending} onCancel={(runId) => cancelMutation.mutate({ mapId, runId })} />}
      {runsQuery.runs.length > 0 && (
        <div className="run-history__footer">
          <span>Showing {runsQuery.runs.length.toLocaleString()} of {Math.max(total, runsQuery.runs.length).toLocaleString()}</span>
          {runsQuery.hasNextPage && (
            <Button variant="secondary" size="sm" disabled={runsQuery.isFetchingNextPage} onClick={() => void runsQuery.fetchNextPage()}>
              {runsQuery.isFetchingNextPage ? 'Loading…' : 'Load older runs'}
            </Button>
          )}
        </div>
      )}
    </div>
  );
}
