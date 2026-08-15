import type { CatalogConnection, CatalogDataset } from '@entities/catalog';
import { Badge, Button, EmptyState, PaddedCard } from '@shared/ui';

export function SourceDatasetList({
  datasets,
  connections,
  previewingId,
  selectedId,
  onPreview,
}: {
  datasets: CatalogDataset[];
  connections: CatalogConnection[];
  previewingId?: string;
  selectedId?: string;
  onPreview: (datasetId: string) => void;
}) {
  if (datasets.length === 0) return <EmptyState title="No datasets yet" description="Define a query and canonical geometry mapping above." />;
  const connectionById = new Map(connections.map((connection) => [connection.id, connection]));
  return (
    <div className="source-datasets">
      {datasets.map((dataset) => {
        const connection = connectionById.get(dataset.draft.connectionId);
        return (
          <PaddedCard className={selectedId === dataset.id ? 'source-dataset source-dataset--selected' : 'source-dataset'} key={dataset.id}>
            <div className="source-dataset__identity">
              <div><strong>{dataset.draft.name}</strong><code>{dataset.draft.slug}</code></div>
              <div className="source-dataset__badges"><Badge>{connection?.adapter ?? 'unknown'}</Badge><Badge tone="info">{dataset.draft.source.geometry.kind}</Badge></div>
            </div>
            <p>{dataset.draft.source.channels.length} typed channels · {connection?.name ?? 'Missing connection'}</p>
            <Button size="sm" variant={selectedId === dataset.id ? 'primary' : 'secondary'} onClick={() => onPreview(dataset.id)} disabled={previewingId === dataset.id}>
              {previewingId === dataset.id ? 'Previewing…' : 'Run bounded preview'}
            </Button>
          </PaddedCard>
        );
      })}
    </div>
  );
}
