import type { BakeRunCounts, BakeRunPage, BakeRunStatus, ManagedBakeRun } from '@/types/bake';
import type { MockRoute } from '../support/mockApi';
import { omitUndefined } from './omitUndefined';

const DAY_MS = 86_400_000;
const NEWEST_RUN = Date.parse('2026-08-14T09:00:00Z');

export const bakeRun = (id: string, overrides: Partial<ManagedBakeRun> = {}): ManagedBakeRun => omitUndefined({
  id,
  draftRevision: 3,
  status: 'succeeded',
  stage: 'succeeded',
  progress: 1,
  createdAt: '2026-08-14T09:00:00Z',
  startedAt: '2026-08-14T09:00:04Z',
  completedAt: '2026-08-14T09:11:48Z',
  sourceRows: 18_402_113,
  tileCount: 21_840,
  artifactBytes: 482_000_000,
  releaseId: `release-${id}`,
  cancellationRequested: false,
  attempt: 1,
  ...overrides,
});

export const runningRun = bakeRun('run-active', {
  status: 'running',
  stage: 'tiling',
  progress: 0.42,
  statusMessage: 'Reducing network-coverage with QuadtreeLod.',
  completedAt: undefined,
  sourceRows: 18_402_113,
  tileCount: undefined,
  artifactBytes: undefined,
  releaseId: undefined,
});

export const queuedRun = bakeRun('run-queued', {
  status: 'queued',
  stage: 'queued',
  progress: 0,
  startedAt: undefined,
  completedAt: undefined,
  sourceRows: undefined,
  tileCount: undefined,
  artifactBytes: undefined,
  releaseId: undefined,
});

const MEMORY_LIMIT_FAILURE = 'Code: 241. DB::Exception: Memory limit (for query) exceeded. (MEMORY_LIMIT_EXCEEDED)';

export const failedRun = bakeRun('run-failed', {
  status: 'failed',
  stage: 'failed',
  progress: 0.3,
  statusMessage: 'Bake failed.',
  error: `ClickHouse 500: ${MEMORY_LIMIT_FAILURE}`,
  failureCategory: 'TransientFailure',
  failureDetails: `source.query: ${MEMORY_LIMIT_FAILURE}`,
  completedAt: '2026-08-14T09:10:06Z',
  attempt: 3,
  tileCount: undefined,
  artifactBytes: undefined,
  releaseId: undefined,
});

export const cancelledRun = bakeRun('run-cancelled', {
  status: 'cancelled',
  stage: 'cancelled',
  progress: 0.12,
  statusMessage: 'Bake cancelled by request.',
  error: 'Bake cancelled by request.',
  failureCategory: 'cancelled',
  cancellationRequested: true,
  completedAt: '2026-08-14T09:02:30Z',
  tileCount: undefined,
  artifactBytes: undefined,
  releaseId: undefined,
});

export function settledRuns(count: number): ManagedBakeRun[] {
  return Array.from({ length: count }, (_, index) => {
    const id = `run-${String(count - index).padStart(4, '0')}`;
    const created = new Date(NEWEST_RUN - (index + 1) * DAY_MS).toISOString();
    const base = { createdAt: created, startedAt: created, completedAt: new Date(Date.parse(created) + 600_000).toISOString() };
    if (index % 7 === 3) return { ...failedRun, ...base, id };
    if (index % 13 === 5) return { ...cancelledRun, ...base, id };
    return bakeRun(id, base);
  });
}

export function countRuns(runs: ManagedBakeRun[]): BakeRunCounts {
  const counts: BakeRunCounts = { queued: 0, running: 0, succeeded: 0, failed: 0, cancelled: 0 };
  for (const run of runs) counts[run.status] += 1;
  return counts;
}

export function runHistoryRoutes(mapId: string, initial: ManagedBakeRun[]): MockRoute[] {
  const runs = initial.map((run) => ({ ...run }));
  return [
    {
      method: 'GET',
      path: `/maps/${mapId}/runs`,
      reply: ({ query }) => {
        const statuses = query.getAll('status') as BakeRunStatus[];
        const limit = Number(query.get('limit'));
        const start = Number(query.get('cursor') ?? 0);
        const matching = runs.filter((run) => statuses.length === 0 || statuses.includes(run.status));
        const next = start + limit;
        const body: BakeRunPage = {
          runs: matching.slice(start, next),
          ...(next < matching.length && { nextCursor: String(next) }),
        };
        return { body };
      },
    },
    {
      method: 'POST',
      path: new RegExp(`^/maps/${mapId}/runs/[^/]+/cancel$`),
      reply: ({ path }) => {
        const run = runs.find((candidate) => candidate.id === path.split('/')[4]);
        if (!run) return { status: 404, body: { error: 'Run not found.' } };
        run.cancellationRequested = true;
        return { body: run };
      },
    },
  ];
}
