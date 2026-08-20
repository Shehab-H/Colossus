import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useLocation, useNavigate } from 'react-router';
import type { ManagedMapDetail } from '@entities/catalog';
import { managedMapQuery } from '@entities/catalog';
import { useArchiveMapMutation, useBakePolicyMutation, useCancelMapRunMutation, usePinMapReleaseMutation, useReconcileMapReleasesMutation, useRunMapMutation } from '@features/map-management/api/mapManagementMutations';
import { hoursToInterval } from '@features/map-management/model/mapForm';
import { embedHref, formatBytes, formatDate, publicationLabel, publicationTone, viewerHref } from '@features/map-management/model/presentation';
import { Alert, Badge, Button, ConfirmDialog, LoadingState, PageHeader, PaddedCard, StatCard, type BadgeTone } from '@shared/ui';
import { ButtonRouteLink } from '@shared/navigation/RouteLink';
import ManagementShell from '@widgets/management-shell/ManagementShell';

function publicationBadgeTone(state: ManagedMapDetail['summary']['publicationState']): BadgeTone {
  const tone = publicationTone(state);
  return tone === 'progress' ? 'info' : tone;
}

export default function MapDetailPage({ mapId }: { mapId: string }) {
  const navigate = useNavigate();
  const location = useLocation();
  const queuedRunId = (location.state as { queuedRunId?: string } | null)?.queuedRunId;
  const detailQuery = useQuery(managedMapQuery(mapId));
  const runMutation = useRunMapMutation();
  const cancelRunMutation = useCancelMapRunMutation();
  const policyMutation = useBakePolicyMutation();
  const archiveMutation = useArchiveMapMutation();
  const pinReleaseMutation = usePinMapReleaseMutation();
  const reconcileMutation = useReconcileMapReleasesMutation();
  const detail = detailQuery.data ?? null;
  const operationError = detailQuery.error ?? runMutation.error ?? cancelRunMutation.error ?? policyMutation.error ?? archiveMutation.error ?? pinReleaseMutation.error ?? reconcileMutation.error;
  const [localError, setLocalError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [confirmArchive, setConfirmArchive] = useState(false);
  const error = localError ?? (operationError instanceof Error ? operationError.message : operationError ? String(operationError) : null);
  const busy = runMutation.isPending ? 'run' : policyMutation.isPending ? 'schedule' : archiveMutation.isPending ? 'archive' : reconcileMutation.isPending ? 'reconcile' : null;

  const runNow = async () => {
    setLocalError(null);
    try { await runMutation.mutateAsync(mapId); }
    catch { /* mutation state owns the rendered error */ }
  };

  const toggleSchedule = async () => {
    if (!detail) return;
    setLocalError(null);
    const current = detail.bakePolicy;
    try {
      await policyMutation.mutateAsync({
        mapId,
        expectedRevision: current?.revision,
        policy: {
          enabled: !(current?.enabled ?? false),
          interval: current?.interval ?? hoursToInterval('24'),
          timeZoneId: current?.timeZoneId ?? 'UTC',
          allowOverlap: current?.allowOverlap ?? false,
          retainSuccessfulReleases: current?.retainSuccessfulReleases ?? 3,
        },
      });
    } catch { /* mutation state owns the rendered error */ }
  };

  const cancelRun = async (runId: string) => {
    setLocalError(null);
    try { await cancelRunMutation.mutateAsync({ mapId, runId }); }
    catch { /* mutation state owns the rendered error */ }
  };

  const pinRelease = async (releaseId: string, pinned: boolean) => {
    setLocalError(null);
    try { await pinReleaseMutation.mutateAsync({ mapId, releaseId, pinned }); }
    catch { /* mutation state owns the rendered error */ }
  };

  const reconcileReleases = async () => {
    setLocalError(null);
    try { await reconcileMutation.mutateAsync(mapId); }
    catch { /* mutation state owns the rendered error */ }
  };

  const archive = async () => {
    if (!detail) return;
    setLocalError(null);
    try {
      await archiveMutation.mutateAsync({ mapId, expectedRevision: detail.summary.revision });
      await navigate('/maps');
    } catch {
      setConfirmArchive(false);
    }
  };

  const copyEmbed = async () => {
    if (!detail) return;
    const url = new URL(embedHref(detail.summary.slug), window.location.origin).toString();
    const snippet = `<iframe src="${url}" title="${detail.summary.title}" loading="lazy" style="width:100%;height:600px;border:0"></iframe>`;
    try { await navigator.clipboard.writeText(snippet); setCopied(true); }
    catch { setLocalError('The browser could not copy the embed snippet.'); }
  };

  return (
    <ManagementShell actions={<ButtonRouteLink variant="ghost" to="/maps">All maps</ButtonRouteLink>}>
      {error && <Alert tone="danger">{error}</Alert>}
      {queuedRunId && <Alert>A bake was queued for the saved configuration. Run ID: <code>{queuedRunId}</code></Alert>}
      {!detail ? <LoadingState>Loading map details…</LoadingState> : (
        <div className="maps-detail">
          <PageHeader
            eyebrow="Map overview"
            title={detail.summary.title}
            description={<span className="maps-detail__identity"><Badge tone={publicationBadgeTone(detail.summary.publicationState)}>{publicationLabel(detail.summary.publicationState)}</Badge><code>{detail.summary.slug}</code><span>revision {detail.summary.revision}</span></span>}
            actions={<>
              {!detail.summary.archived && <ButtonRouteLink variant="secondary" to={`/maps/manage/${mapId}/edit`}>Edit</ButtonRouteLink>}
              {!detail.summary.archived && <ButtonRouteLink variant="secondary" to={viewerHref(detail.summary.slug)}>Open viewer</ButtonRouteLink>}
              {!detail.summary.archived && <Button variant="primary" onClick={() => void copyEmbed()}>{copied ? 'Embed copied' : 'Copy embed'}</Button>}
            </>}
          />

          <section className="maps-detail__stats" aria-label="Map status">
            <StatCard label="Source" value={detail.summary.sourceType} note={detail.dataset.name} />
            <StatCard label="Current release" value={detail.summary.currentRelease ?? 'None'} note={formatDate(detail.summary.publishedAt)} />
            <StatCard label="Last run" value={detail.summary.lastRunStatus ?? 'No runs'} note={formatDate(detail.summary.lastRunAt)} danger={Boolean(detail.summary.lastFailure)} />
            <StatCard label="Next run" value={detail.summary.scheduleEnabled ? formatDate(detail.summary.nextRunAt) : 'Disabled'} note={detail.bakePolicy ? `retain ${detail.bakePolicy.retainSuccessfulReleases}` : 'No policy'} />
            <StatCard label="Output" value={formatBytes(detail.summary.outputBytes)} note="Release metadata" />
          </section>

          {detail.summary.lastFailure && <Alert tone="danger"><strong>Last failure:</strong> {detail.summary.lastFailure}</Alert>}

          <div className="maps-detail__grid">
            <PaddedCard className="maps-detail__card maps-detail__card--preview">
              <CardHeading eyebrow="Preview" title="Published map" action={<ButtonRouteLink variant="ghost" size="sm" to={viewerHref(detail.summary.slug)}>Open full viewer</ButtonRouteLink>} />
              {detail.summary.archived
                ? <p className="maps-detail__note">Archived maps cannot open a new viewer.</p>
                : <iframe
                    key={detail.summary.currentRelease ?? `unpublished-${detail.summary.revision}`}
                    className="maps-detail__preview"
                    src={embedHref(detail.summary.slug)}
                    title={`${detail.summary.title} preview`}
                    loading="lazy"
                  />}
            </PaddedCard>

            <PaddedCard className="maps-detail__card">
              <CardHeading eyebrow="Operations" title="Bake policy" />
              <DefinitionList items={[
                ['Recurring bake', detail.bakePolicy?.enabled ? 'Enabled' : 'Disabled'],
                ['Interval', detail.bakePolicy?.interval ?? '—'],
                ['Time zone', detail.bakePolicy?.timeZoneId ?? 'UTC'],
                ['Retention', `${detail.bakePolicy?.retainSuccessfulReleases ?? 3} releases`],
              ]} />
              {!detail.summary.archived && <div className="maps-detail__stack"><Button variant="primary" disabled={busy === 'run'} onClick={() => void runNow()}>{busy === 'run' ? 'Queueing…' : 'Run now'}</Button><Button variant="secondary" disabled={busy === 'schedule'} onClick={() => void toggleSchedule()}>{busy === 'schedule' ? 'Saving…' : detail.bakePolicy?.enabled ? 'Disable schedule' : 'Enable schedule'}</Button><p>Run now captures an immutable snapshot and hands it to the persistent bake queue.</p></div>}
            </PaddedCard>

            <PaddedCard className="maps-detail__card">
              <CardHeading eyebrow="Source contract" title={detail.dataset.name} />
              <DefinitionList items={[["Adapter", detail.dataset.sourceType], ["Geometry", detail.dataset.source.geometry.kind], ["Channels", String(detail.dataset.source.channels.length)]]} />
              <details className="maps-detail__source"><summary>View query and channel contract</summary><pre>{detail.dataset.source.query}</pre><pre>{JSON.stringify(detail.dataset.source.geometry, null, 2)}</pre></details>
            </PaddedCard>

            <PaddedCard className="maps-detail__card maps-detail__card--wide">
              <CardHeading eyebrow="History" title="Bake runs" />
              {detail.runs.length === 0 ? <p className="maps-detail__note">This map has never been queued.</p> : <div className="maps-detail__history">{detail.runs.map((run) => {
                const active = run.status === 'queued' || run.status === 'running';
                return <div className="maps-detail__run" key={run.id}>
                  <div className="maps-detail__history-row">
                    <Badge tone={run.status === 'failed' ? 'danger' : run.status === 'succeeded' ? 'success' : run.status === 'cancelled' ? 'neutral' : 'info'}>{run.status}</Badge>
                    <strong>{run.stage} · {Math.round(run.progress * 100)}%</strong>
                    <span>{formatDate(run.completedAt ?? run.startedAt ?? run.createdAt)}</span>
                    {active && !run.cancellationRequested
                      ? <Button variant="ghost" size="sm" disabled={cancelRunMutation.isPending} onClick={() => void cancelRun(run.id)}>Cancel</Button>
                      : <span>{run.statusMessage ?? run.error}</span>}
                  </div>
                  {active && <progress className="maps-detail__progress" max={1} value={run.progress} aria-label={`${run.stage} progress`} />}
                  {(run.failureCategory || run.failureDetails) && <Alert tone="danger"><strong>{run.failureCategory ?? 'Bake failure'}:</strong> {run.failureDetails ?? run.error}</Alert>}
                  {(run.sourceRows !== undefined || run.tileCount !== undefined || run.artifactBytes !== undefined) && <p className="maps-detail__run-metrics">{run.sourceRows?.toLocaleString() ?? '—'} rows · {run.tileCount?.toLocaleString() ?? '—'} tiles · {formatBytes(run.artifactBytes)} · attempt {run.attempt}</p>}
                </div>;
              })}</div>}
            </PaddedCard>

            <PaddedCard className="maps-detail__card maps-detail__card--wide">
              <CardHeading eyebrow="Publication" title="Releases" action={<Button variant="ghost" size="sm" disabled={busy === 'reconcile'} onClick={() => void reconcileReleases()}>{busy === 'reconcile' ? 'Reconciling…' : 'Run retention'}</Button>} />
              {detail.releases.length === 0 ? <p className="maps-detail__note">No catalog-tracked release has been published yet.</p> : <div className="maps-detail__history">{detail.releases.map((release) => <div className="maps-detail__history-row maps-detail__history-row--release" key={release.id}><strong>{release.version}</strong>{release.pinned && <Badge tone="info">Pinned</Badge>}<span>{formatDate(release.publishedAt)}</span><span>{formatBytes(release.outputBytes)}</span><span>{release.sourceRows?.toLocaleString() ?? '—'} rows · {release.tileCount?.toLocaleString() ?? '—'} tiles</span><Badge tone={release.verificationPassed ? 'success' : 'neutral'}>{release.verificationPassed ? 'Verified' : 'Legacy / unverified'}</Badge><Button variant="ghost" size="sm" disabled={pinReleaseMutation.isPending} onClick={() => void pinRelease(release.id, !release.pinned)}>{release.pinned ? 'Unpin' : 'Pin'}</Button><code>{release.artifactLocation}</code></div>)}</div>}
            </PaddedCard>
          </div>

          {!detail.summary.archived && <section className="maps-detail__danger"><div><h2>Archive map</h2><p>Remove it from the active catalog without deleting published artifacts.</p></div><Button variant="danger" onClick={() => setConfirmArchive(true)}>Archive map</Button></section>}

          <ConfirmDialog open={confirmArchive} title="Archive this map?" description={<>“{detail.summary.title}” will leave the active catalog. Published releases will not be deleted.</>} confirmLabel="Archive map" busy={busy === 'archive'} onCancel={() => setConfirmArchive(false)} onConfirm={() => void archive()} />
        </div>
      )}
    </ManagementShell>
  );
}

function CardHeading({ eyebrow, title, action }: { eyebrow: string; title: string; action?: React.ReactNode }) {
  return <div className="maps-detail__card-heading"><div><span>{eyebrow}</span><h2>{title}</h2></div>{action}</div>;
}

function DefinitionList({ items }: { items: Array<[string, string]> }) {
  return <dl className="maps-detail__definitions">{items.map(([term, value]) => <div key={term}><dt>{term}</dt><dd>{value}</dd></div>)}</dl>;
}
