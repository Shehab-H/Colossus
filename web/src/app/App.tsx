import { useEffect, useState } from 'react';
import MapDetailPage from '@pages/maps/MapDetailPage';
import MapEditorPage from '@pages/maps/MapEditorPage';
import MapCatalogPage from '@pages/maps/MapCatalogPage';
import { resolveManagementRoute, type ManagementRoute } from '@features/map-management/model/managementRoute';
import MapViewerPage from '@pages/maps/MapViewerPage';
import {
  canonicalViewerUrl,
  resolveViewerRoute,
  type ViewerRoute,
} from '@features/map-viewer/model/viewRoute';
import { Badge, ButtonLink, PaddedCard } from '@shared/ui';

type ApplicationRoute =
  | { surface: 'management'; route: ManagementRoute }
  | { surface: 'viewer'; route: ViewerRoute };

function currentRoute(): ApplicationRoute | null {
  const management = resolveManagementRoute(window.location.pathname);
  if (management) return { surface: 'management', route: management };
  const viewer = resolveViewerRoute(window.location.href);
  return viewer ? { surface: 'viewer', route: viewer } : null;
}

/** Application composition root. Management owns catalog state; the renderer still receives exactly one map id. */
export default function App() {
  const [applicationRoute, setApplicationRoute] = useState<ApplicationRoute | null>(currentRoute);

  useEffect(() => {
    const readLocation = () => {
      const next = currentRoute();
      if (next?.surface === 'viewer' && next.route.legacy) {
        const canonical = canonicalViewerUrl(window.location.href, next.route);
        window.history.replaceState(null, '', canonical);
        setApplicationRoute(currentRoute());
      } else {
        setApplicationRoute(next);
      }
    };
    readLocation();
    window.addEventListener('popstate', readLocation);
    return () => window.removeEventListener('popstate', readLocation);
  }, []);

  if (!applicationRoute) {
    return (
      <main className="route-empty">
        <PaddedCard className="route-empty__card">
          <Badge tone="brand">Colossus Maps</Badge>
          <h1>Open the map catalog</h1>
          <p>Manage maps at <a href="/maps">/maps</a> or open an individual viewer such as <code>/maps/geonames</code>.</p>
          <div className="route-empty__actions"><ButtonLink variant="primary" href="/maps">Open catalog</ButtonLink></div>
        </PaddedCard>
      </main>
    );
  }

  if (applicationRoute.surface === 'management') {
    const route = applicationRoute.route;
    if (route.kind === 'list') return <MapCatalogPage />;
    if (route.kind === 'create') return <MapEditorPage />;
    if (route.kind === 'edit') return <MapEditorPage mapId={route.mapId} />;
    return <MapDetailPage mapId={route.mapId} />;
  }

  const route = applicationRoute.route;
  return <MapViewerPage key={`${route.surface}:${route.mapId}`} mapId={route.mapId} surface={route.surface} />;
}
