import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { useArchiveMapMutation, MapCatalogTable, catalogContentState } from '@/features/maps';
import { Alert, Checkbox, ConfirmDialog, EmptyState, LoadingState, PageHeader } from '@/components/ui';
import { ButtonRouteLink } from '@/components/ui/RouteLink';
import ManagementLayout from '@/components/layouts/ManagementLayout';
import type { ManagedMapSummary } from '@/types/map';
import { managedMapsQuery } from '@/api/maps';

export default function MapCatalogPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const includeArchived = searchParams.get('archived') === '1';
  const mapsQuery = useQuery(managedMapsQuery(includeArchived));
  const archiveMutation = useArchiveMapMutation();
  const [pendingArchive, setPendingArchive] = useState<ManagedMapSummary | null>(null);
  const maps = mapsQuery.data ?? null;
  const error = mapsQuery.error ?? archiveMutation.error;
  const busyId = archiveMutation.isPending ? archiveMutation.variables?.mapId ?? null : null;
  const contentState = catalogContentState(maps?.length ?? null, error !== null);

  const confirmArchive = async () => {
    if (!pendingArchive) return;
    try {
      await archiveMutation.mutateAsync({ mapId: pendingArchive.id, expectedRevision: pendingArchive.revision });
      setPendingArchive(null);
    } catch { /* mutation state owns the rendered error */ }
  };

  const toggleArchived = (checked: boolean) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      if (checked) next.set('archived', '1');
      else next.delete('archived');
      return next;
    });
  };

  return (
    <ManagementLayout actions={<><ButtonRouteLink variant="ghost" to="/sources">Sources</ButtonRouteLink><ButtonRouteLink variant="primary" to="/maps/new">Create map</ButtonRouteLink></>}>
      <PageHeader
        eyebrow="Control plane"
        title="Maps"
        description="Manage map definitions, bake runs, releases, schedules, and archived records."
        actions={<Checkbox label="Show archived" checked={includeArchived} onChange={(event) => toggleArchived(event.target.checked)} />}
      />

      {error && <Alert tone="danger">{error instanceof Error ? error.message : String(error)}</Alert>}

      <div className="maps-page__content">
        {contentState === 'loading' ? (
          <LoadingState>Loading map catalog…</LoadingState>
        ) : contentState === 'empty' ? (
          <EmptyState
            eyebrow="No maps yet"
            title="Create the first map"
            description="Choose a migrated dataset, configure its view, then publish it when the bake workflow is connected."
            action={<ButtonRouteLink variant="primary" to="/maps/new">Create map</ButtonRouteLink>}
          />
        ) : contentState === 'ready' && maps ? (
          <MapCatalogTable maps={maps} busyId={busyId} onArchive={setPendingArchive} />
        ) : null}
      </div>

      <ConfirmDialog
        open={pendingArchive !== null}
        title="Archive this map?"
        description={<>“{pendingArchive?.title}” will leave the active catalog. Existing published artifacts remain intact.</>}
        confirmLabel="Archive map"
        busy={archiveMutation.isPending}
        onCancel={() => { setPendingArchive(null); archiveMutation.reset(); }}
        onConfirm={() => void confirmArchive()}
      />
    </ManagementLayout>
  );
}
