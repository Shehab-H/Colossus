import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import type { ManagedMapSummary } from '@entities/catalog';
import { managedMapsQuery } from '@entities/catalog';
import { useArchiveMapMutation } from '@features/map-management/api/mapManagementMutations';
import { catalogContentState } from '@features/map-management/model/presentation';
import { Alert, Checkbox, ConfirmDialog, EmptyState, LoadingState, PageHeader } from '@shared/ui';
import { ButtonRouteLink } from '@shared/navigation/RouteLink';
import MapCatalogTable from '@widgets/map-catalog/MapCatalogTable';
import ManagementShell from '@widgets/management-shell/ManagementShell';

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
    <ManagementShell actions={<><ButtonRouteLink variant="ghost" to="/sources">Sources</ButtonRouteLink><ButtonRouteLink variant="primary" to="/maps/new">Create map</ButtonRouteLink></>}>
      <PageHeader
        eyebrow="Control plane"
        title="Your map estate, in one place."
        description="Create, publish, schedule, and inspect high-volume maps without changing registry files."
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
    </ManagementShell>
  );
}
