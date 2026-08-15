import { useParams } from 'react-router';
import MapViewerPage from '@pages/maps/MapViewerPage';
import '@widgets/map-viewer/viewer-shell.scss';

function requiredMapId(mapId: string | undefined): string {
  if (!mapId) throw new Response('A map id is required.', { status: 400 });
  return mapId;
}

export function MapViewerRoute() {
  const { mapId } = useParams();
  const id = requiredMapId(mapId);
  return <MapViewerPage key={`map:${id}`} mapId={id} surface="map" />;
}

export function EmbedViewerRoute() {
  const { mapId } = useParams();
  const id = requiredMapId(mapId);
  return <MapViewerPage key={`embed:${id}`} mapId={id} surface="embed" />;
}
