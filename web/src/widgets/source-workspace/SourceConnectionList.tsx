import type { CatalogConnection, SourceConnectionValidation, SourceLocation } from '@entities/catalog';
import { Badge, Button, EmptyState, PaddedCard } from '@shared/ui';

export function SourceConnectionList({
  connections,
  testingId,
  discoveringId,
  validation,
  locations,
  onTest,
  onDiscover,
}: {
  connections: CatalogConnection[];
  testingId?: string;
  discoveringId?: string;
  validation?: { connectionId: string; result: SourceConnectionValidation };
  locations?: { connectionId: string; result: SourceLocation[] };
  onTest: (connectionId: string) => void;
  onDiscover: (connectionId: string) => void;
}) {
  if (connections.length === 0) return <EmptyState title="No connections yet" description="Add a source connection to start discovering datasets." />;
  return (
    <div className="source-connections">
      {connections.map((connection) => (
        <PaddedCard className="source-connection" key={connection.id}>
          <div className="source-connection__heading">
            <div><strong>{connection.name}</strong><code>{connection.slug}</code></div>
            <Badge tone="brand">{connection.adapter}</Badge>
          </div>
          <div className="source-connection__actions">
            <Button size="sm" onClick={() => onTest(connection.id)} disabled={testingId === connection.id}>
              {testingId === connection.id ? 'Testing…' : 'Test connection'}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => onDiscover(connection.id)} disabled={discoveringId === connection.id}>
              {discoveringId === connection.id ? 'Discovering…' : 'Discover locations'}
            </Button>
          </div>
          {validation?.connectionId === connection.id && (
            <div className="source-connection__result" role="status">
              <strong>{validation.result.message}</strong>
              {Object.entries(validation.result.details).map(([key, value]) => <span key={key}>{key}: {value}</span>)}
            </div>
          )}
          {locations?.connectionId === connection.id && (
            <div className="source-location-list">
              {locations.result.length === 0 ? <span>No locations returned.</span> : locations.result.map((location) => (
                <span key={`${location.namespace}.${location.name}`}><code>{location.namespace}.{location.name}</code><small>{location.kind}</small></span>
              ))}
            </div>
          )}
        </PaddedCard>
      ))}
    </div>
  );
}
