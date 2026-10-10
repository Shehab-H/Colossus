import { useParams } from 'react-router';
import MapViewerPage from '@/app/routes/viewer/MapViewerPage';
import '@/features/map-viewer/viewer.scss';
import '@/app/routes/viewer/MapViewerPage.scss';

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
