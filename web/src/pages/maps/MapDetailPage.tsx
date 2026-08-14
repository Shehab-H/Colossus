import { useCallback, useEffect, useState } from 'react';
import type { ManagedMapDetail } from '@entities/catalog';
import { archiveMap, getManagedMap, runMapNow, setBakePolicy } from '@entities/catalog';
import { hoursToInterval } from '@features/map-management/model/mapForm';
import { embedHref, formatBytes, formatDate, publicationLabel, publicationTone, viewerHref } from '@features/map-management/model/presentation';
import { Alert, Badge, Button, ButtonLink, ConfirmDialog, LoadingState, PageHeader, PaddedCard, StatCard, type BadgeTone } from '@shared/ui';
import ManagementShell from '@widgets/management-shell/ManagementShell';

function publicationBadgeTone(state: ManagedMapDetail['summary']['publicationState']): BadgeTone {
  const tone = publicationTone(state);
  return tone === 'progress' ? 'info' : tone;
}

export default function MapDetailPage({ mapId }: { mapId: string }) {
  const [detail, setDetail] = useState<ManagedMapDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [confirmArchive, setConfirmArchive] = useState(false);

  const load = useCallback(() => {
    setError(null);
    getManagedMap(mapId).then(setDetail, (reason) => setError(reason instanceof Error ? reason.message : String(reason)));
  }, [mapId]);

  useEffect(load, [load]);

  const runNow = async () => {
    setBusy('run');
    try { await runMapNow(mapId); load(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(null); }
  };

  const toggleSchedule = async () => {
    if (!detail) return;
    setBusy('schedule');
    const current = detail.bakePolicy;
    try {
      await setBakePolicy(mapId, {
        enabled: !(current?.enabled ?? false),
        interval: current?.interval ?? hoursToInterval('24'),
        timeZoneId: current?.timeZoneId ?? 'UTC',
        allowOverlap: current?.allowOverlap ?? false,
        retainSuccessfulReleases: current?.retainSuccessfulReleases ?? 3,
      }, current?.revision);
      load();
    } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(null); }
  };

  const archive = async () => {
    if (!detail) return;
    setBusy('archive');
    try {
      await archiveMap(mapId, detail.summary.revision);
      window.location.assign('/maps');
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
      setBusy(null);
      setConfirmArchive(false);
    }
  };

  const copyEmbed = async () => {
    if (!detail) return;
    const url = new URL(embedHref(detail.summary.slug), window.location.origin).toString();
    const snippet = `<iframe src="${url}" title="${detail.summary.title}" loading="lazy" style="width:100%;height:600px;border:0"></iframe>`;
    try { await navigator.clipboard.writeText(snippet); setCopied(true); }
    catch { setError('The browser could not copy the embed snippet.'); }
  };

  return (
    <ManagementShell actions={<ButtonLink variant="ghost" href="/maps">All maps</ButtonLink>}>
      {error && <Alert tone="danger">{error}</Alert>}
      {!detail ? <LoadingState>Loading map details…</LoadingState> : (
        <div className="maps-detail">
          <PageHeader
            eyebrow="Map overview"
            title={detail.summary.title}
            description={<span className="maps-detail__identity"><Badge tone={publicationBadgeTone(detail.summary.publicationState)}>{publicationLabel(detail.summary.publicationState)}</Badge><code>{detail.summary.slug}</code><span>revision {detail.summary.revision}</span></span>}
            actions={<>
              {!detail.summary.archived && <ButtonLink variant="secondary" href={`/maps/manage/${mapId}/edit`}>Edit</ButtonLink>}
              {!detail.summary.archived && <ButtonLink variant="secondary" href={viewerHref(detail.summary.slug)}>Open viewer</ButtonLink>}
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
              <CardHeading eyebrow="Preview" title="Published map" action={<ButtonLink variant="ghost" size="sm" href={viewerHref(detail.summary.slug)}>Open full viewer</ButtonLink>} />
              {detail.summary.archived ? <p className="maps-detail__note">Archived maps cannot open a new viewer.</p> : <iframe className="maps-detail__preview" src={embedHref(detail.summary.slug)} title={`${detail.summary.title} preview`} loading="lazy" />}
            </PaddedCard>

            <PaddedCard className="maps-detail__card">
              <CardHeading eyebrow="Operations" title="Bake policy" />
              <DefinitionList items={[
                ['Recurring bake', detail.bakePolicy?.enabled ? 'Enabled' : 'Disabled'],
                ['Interval', detail.bakePolicy?.interval ?? '—'],
                ['Time zone', detail.bakePolicy?.timeZoneId ?? 'UTC'],
                ['Retention', `${detail.bakePolicy?.retainSuccessfulReleases ?? 3} releases`],
              ]} />
              {!detail.summary.archived && <div className="maps-detail__stack"><Button variant="primary" disabled={busy === 'run'} onClick={() => void runNow()}>{busy === 'run' ? 'Queueing…' : 'Run now'}</Button><Button variant="secondary" disabled={busy === 'schedule'} onClick={() => void toggleSchedule()}>{busy === 'schedule' ? 'Saving…' : detail.bakePolicy?.enabled ? 'Disable schedule' : 'Enable schedule'}</Button><p>Run now captures an immutable queued snapshot. Worker execution arrives with scheduling.</p></div>}
            </PaddedCard>

            <PaddedCard className="maps-detail__card">
              <CardHeading eyebrow="Source contract" title={detail.dataset.name} />
              <DefinitionList items={[["Adapter", detail.dataset.sourceType], ["Geometry", detail.dataset.source.geometry.kind], ["Channels", String(detail.dataset.source.channels.length)]]} />
              <details className="maps-detail__source"><summary>View query and channel contract</summary><pre>{detail.dataset.source.query}</pre><pre>{JSON.stringify(detail.dataset.source.geometry, null, 2)}</pre></details>
            </PaddedCard>

            <PaddedCard className="maps-detail__card maps-detail__card--wide">
              <CardHeading eyebrow="History" title="Bake runs" />
              {detail.runs.length === 0 ? <p className="maps-detail__note">This map has never been queued.</p> : <div className="maps-detail__history">{detail.runs.map((run) => <div className="maps-detail__history-row" key={run.id}><Badge tone={run.status === 'failed' ? 'danger' : run.status === 'succeeded' ? 'success' : 'info'}>{run.status}</Badge><strong>Draft r{run.draftRevision}</strong><span>{formatDate(run.completedAt ?? run.startedAt ?? run.createdAt)}</span><span>{run.error}</span></div>)}</div>}
            </PaddedCard>

            <PaddedCard className="maps-detail__card maps-detail__card--wide">
              <CardHeading eyebrow="Publication" title="Releases" />
              {detail.releases.length === 0 ? <p className="maps-detail__note">No catalog-tracked release has been published yet.</p> : <div className="maps-detail__history">{detail.releases.map((release) => <div className="maps-detail__history-row" key={release.id}><strong>{release.version}</strong><span>{formatDate(release.publishedAt)}</span><span>{formatBytes(release.outputBytes)}</span><code>{release.artifactLocation}</code></div>)}</div>}
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
