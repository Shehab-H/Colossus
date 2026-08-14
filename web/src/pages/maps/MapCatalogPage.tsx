import { useCallback, useEffect, useState } from 'react';
import type { ManagedMapSummary } from '@entities/catalog';
import { archiveMap, listManagedMaps } from '@entities/catalog';
import { catalogContentState } from '@features/map-management/model/presentation';
import { Alert, ButtonLink, Checkbox, ConfirmDialog, EmptyState, LoadingState, PageHeader } from '@shared/ui';
import MapCatalogTable from '@widgets/map-catalog/MapCatalogTable';
import ManagementShell from '@widgets/management-shell/ManagementShell';

export default function MapCatalogPage() {
  const [maps, setMaps] = useState<ManagedMapSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [includeArchived, setIncludeArchived] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [pendingArchive, setPendingArchive] = useState<ManagedMapSummary | null>(null);
  const contentState = catalogContentState(maps?.length ?? null, error !== null);

  const load = useCallback(() => {
    setError(null);
    listManagedMaps(includeArchived).then(setMaps, (reason) => {
      setMaps([]);
      setError(reason instanceof Error ? reason.message : String(reason));
    });
  }, [includeArchived]);

  useEffect(load, [load]);

  const confirmArchive = async () => {
    if (!pendingArchive) return;
    setBusyId(pendingArchive.id);
    try {
      await archiveMap(pendingArchive.id, pendingArchive.revision);
      setPendingArchive(null);
      load();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <ManagementShell actions={<ButtonLink variant="primary" href="/maps/new">Create map</ButtonLink>}>
      <PageHeader
        eyebrow="Control plane"
        title="Your map estate, in one place."
        description="Create, publish, schedule, and inspect high-volume maps without changing registry files."
        actions={<Checkbox label="Show archived" checked={includeArchived} onChange={(event) => setIncludeArchived(event.target.checked)} />}
      />

      {error && <Alert tone="danger">{error}</Alert>}

      <div className="maps-page__content">
        {contentState === 'loading' ? (
          <LoadingState>Loading map catalog…</LoadingState>
        ) : contentState === 'empty' ? (
          <EmptyState
            eyebrow="No maps yet"
            title="Create the first map"
            description="Choose a migrated dataset, configure its view, then publish it when the bake workflow is connected."
            action={<ButtonLink variant="primary" href="/maps/new">Create map</ButtonLink>}
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
        busy={busyId !== null}
        onCancel={() => setPendingArchive(null)}
        onConfirm={() => void confirmArchive()}
      />
    </ManagementShell>
  );
}
