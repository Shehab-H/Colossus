import { useParams } from 'react-router';
import MapCatalogPage from '@pages/maps/MapCatalogPage';
import MapDetailPage from '@pages/maps/MapDetailPage';
import MapEditorPage from '@pages/maps/MapEditorPage';
import SourceWorkspacePage from '@pages/sources/SourceWorkspacePage';

function requiredMapId(mapId: string | undefined): string {
  if (!mapId) throw new Response('A map id is required.', { status: 400 });
  return mapId;
}

export function CatalogRoute() {
  return <MapCatalogPage />;
}

export function SourcesRoute() {
  return <SourceWorkspacePage />;
}

export function CreateMapRoute() {
  return <MapEditorPage />;
}

export function EditMapRoute() {
  const { mapId } = useParams();
  return <MapEditorPage mapId={requiredMapId(mapId)} />;
}

export function MapDetailRoute() {
  const { mapId } = useParams();
  return <MapDetailPage mapId={requiredMapId(mapId)} />;
}
