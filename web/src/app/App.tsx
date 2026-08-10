import { useEffect, useState } from 'react';
import MapViewerPage from '@features/map-viewer/MapViewerPage';
import {
  canonicalViewerUrl,
  resolveViewerRoute,
  type ViewerRoute,
} from '@features/map-viewer/model/viewRoute';

function currentRoute(): ViewerRoute | null {
  return resolveViewerRoute(window.location.href);
}

/** Application composition root. The renderer receives exactly one map id and never loads management state. */
export default function App() {
  const [route, setRoute] = useState<ViewerRoute | null>(currentRoute);

  useEffect(() => {
    const readLocation = () => {
      const next = currentRoute();
      if (next?.legacy) {
        const canonical = canonicalViewerUrl(window.location.href, next);
        window.history.replaceState(null, '', canonical);
        setRoute(resolveViewerRoute(canonical));
      } else {
        setRoute(next);
      }
    };
    readLocation();
    window.addEventListener('popstate', readLocation);
    return () => window.removeEventListener('popstate', readLocation);
  }, []);

  if (!route) {
    return (
      <main className="route-empty">
        <div className="route-empty__card">
          <span className="eyebrow">Colossus Maps</span>
          <h1>Choose a map from the maps page</h1>
          <p>Individual viewers have stable addresses such as <code>/maps/geonames</code>.</p>
        </div>
      </main>
    );
  }

  return <MapViewerPage key={`${route.surface}:${route.mapId}`} mapId={route.mapId} surface={route.surface} />;
}
