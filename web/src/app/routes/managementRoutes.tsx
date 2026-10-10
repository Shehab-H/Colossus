import { useParams } from 'react-router';
import MapCatalogPage from '@/app/routes/maps/MapCatalogPage';
import MapDetailPage from '@/app/routes/maps/MapDetailPage';
import MapEditorPage from '@/app/routes/maps/MapEditorPage';
import SourcesPage from '@/app/routes/sources/SourcesPage';

function requiredMapId(mapId: string | undefined): string {
  if (!mapId) throw new Response('A map id is required.', { status: 400 });
  return mapId;
}

export function CatalogRoute() {
  return <MapCatalogPage />;
}

export function SourcesRoute() {
  return <SourcesPage />;
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
