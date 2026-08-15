import type { ManagedMapSummary } from '@entities/catalog';
import { formatBytes, formatDate, publicationLabel, publicationTone, viewerHref } from '@features/map-management/model/presentation';
import { Badge, Button, Card, type BadgeTone } from '@shared/ui';
import { ButtonRouteLink, RouteLink } from '@shared/navigation/RouteLink';

function stateTone(state: ManagedMapSummary['publicationState']): BadgeTone {
  const tone = publicationTone(state);
  if (tone === 'progress') return 'info';
  return tone;
}

export default function MapCatalogTable({ maps, busyId, onArchive }: {
  maps: ManagedMapSummary[];
  busyId: string | null;
  onArchive: (map: ManagedMapSummary) => void;
}) {
  return (
    <Card className="map-catalog">
      <div className="map-catalog__scroll">
        <table className="map-catalog__table">
          <thead>
            <tr>
              <th>Map</th>
              <th>Source</th>
              <th>State</th>
              <th>Current release</th>
              <th>Last run</th>
              <th>Next run</th>
              <th>Output</th>
              <th><span className="ui-sr-only">Actions</span></th>
            </tr>
          </thead>
          <tbody>
            {maps.map((map) => (
              <tr key={map.id} className={map.archived ? 'is-muted' : undefined}>
                <td data-label="Map">
                  <RouteLink className="map-catalog__title" to={`/maps/manage/${map.id}`}>{map.title}</RouteLink>
                  <span className="map-catalog__meta">{map.slug}</span>
                  {map.lastFailure && <span className="map-catalog__failure" title={map.lastFailure}>{map.lastFailure}</span>}
                </td>
                <td data-label="Source"><Badge>{map.sourceType}</Badge></td>
                <td data-label="State"><Badge tone={stateTone(map.publicationState)}>{publicationLabel(map.publicationState)}</Badge></td>
                <td data-label="Current release">
                  <strong>{map.currentRelease ?? '—'}</strong>
                  <span className="map-catalog__meta">{formatDate(map.publishedAt)}</span>
                </td>
                <td data-label="Last run">
                  <span>{map.lastRunStatus ?? 'No runs'}</span>
                  <span className="map-catalog__meta">{formatDate(map.lastRunAt)}</span>
                </td>
                <td data-label="Next run">{map.scheduleEnabled ? formatDate(map.nextRunAt) : 'Disabled'}</td>
                <td data-label="Output">{formatBytes(map.outputBytes)}</td>
                <td>
                  <div className="map-catalog__actions">
                    {!map.archived && <ButtonRouteLink variant="ghost" size="sm" to={viewerHref(map.slug)}>Open</ButtonRouteLink>}
                    <ButtonRouteLink variant="ghost" size="sm" to={`/maps/manage/${map.id}`}>Details</ButtonRouteLink>
                    {!map.archived && <ButtonRouteLink variant="ghost" size="sm" to={`/maps/manage/${map.id}/edit`}>Edit</ButtonRouteLink>}
                    {!map.archived && <Button variant="ghost" size="sm" disabled={busyId === map.id} onClick={() => onArchive(map)}>Archive</Button>}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}
