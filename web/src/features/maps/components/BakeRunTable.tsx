import { useState } from 'react';
import { Badge, Button } from '@/components/ui';
import type { ManagedBakeRun } from '@/types/bake';
import { formatBytes, formatDate, formatDuration } from '@/utils/format';
import { isActiveRun, runFailureDetails, runOutcome, runStatusTone } from '../utils/bakeRunStatus';

const COLUMN_COUNT = 7;

export default function BakeRunTable({ runs, cancelling, onCancel }: {
  runs: ManagedBakeRun[];
  cancelling: boolean;
  onCancel: (runId: string) => void;
}) {
  return (
    <div className="run-history__scroll">
      <table className="run-history__table">
        <thead>
          <tr>
            <th>Status</th>
            <th>Started</th>
            <th>Duration</th>
            <th className="is-numeric">Rows</th>
            <th className="is-numeric">Tiles</th>
            <th className="is-numeric">Output</th>
            <th>Result</th>
          </tr>
        </thead>
        {runs.map((run) => <BakeRunRow key={run.id} run={run} cancelling={cancelling} onCancel={onCancel} />)}
      </table>
    </div>
  );
}

function BakeRunRow({ run, cancelling, onCancel }: {
  run: ManagedBakeRun;
  cancelling: boolean;
  onCancel: (runId: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const active = isActiveRun(run);
  const details = runFailureDetails(run);
  const finished = run.status === 'succeeded';

  return (
    <tbody className="run-history__run">
      <tr>
        <td>
          <Badge tone={runStatusTone(run.status)}>{run.status}</Badge>
          {active && <progress className="run-history__progress" max={1} value={run.progress} aria-label={`${run.stage} progress`} />}
        </td>
        <td>
          <span>{formatDate(run.startedAt ?? run.createdAt)}</span>
          <span className="run-history__meta">revision {run.draftRevision} · attempt {run.attempt}</span>
        </td>
        <td>{active ? `${run.stage} · ${Math.round(run.progress * 100)}%` : formatDuration(run.startedAt, run.completedAt)}</td>
        <td className="is-numeric">{run.sourceRows?.toLocaleString() ?? '—'}</td>
        <td className="is-numeric">{run.tileCount?.toLocaleString() ?? '—'}</td>
        <td className="is-numeric">{finished ? formatBytes(run.artifactBytes) : '—'}</td>
        <td>
          <div className="run-history__result">
            <span className={run.status === 'failed' ? 'run-history__outcome is-danger' : 'run-history__outcome'} title={runOutcome(run)}>{runOutcome(run) ?? '—'}</span>
            {details && <Button variant="ghost" size="sm" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>{expanded ? 'Hide' : 'Details'}</Button>}
            {active && !run.cancellationRequested && <Button variant="ghost" size="sm" disabled={cancelling} onClick={() => onCancel(run.id)}>Cancel</Button>}
          </div>
        </td>
      </tr>
      {expanded && details && (
        <tr className="run-history__details">
          <td colSpan={COLUMN_COUNT}><pre>{details}</pre></td>
        </tr>
      )}
    </tbody>
  );
}
