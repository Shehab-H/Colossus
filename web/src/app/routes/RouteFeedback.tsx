import { isRouteErrorResponse, useRouteError } from 'react-router';
import { ButtonRouteLink, RouteLink } from '@/components/ui/RouteLink';
import { Badge, PaddedCard } from '@/components/ui';

export function RouteFallback() {
  return (
    <main className="route-empty">
      <PaddedCard className="route-empty__card">Loading route…</PaddedCard>
    </main>
  );
}

export function RouteNotFound() {
  return (
    <main className="route-empty">
      <PaddedCard className="route-empty__card">
        <Badge tone="brand">Colossus Maps</Badge>
        <h1>Open the map catalog</h1>
        <p>Manage maps at <RouteLink to="/maps">/maps</RouteLink> or open an individual viewer such as <code>/maps/geonames</code>.</p>
        <div className="route-empty__actions"><ButtonRouteLink variant="primary" to="/maps">Open catalog</ButtonRouteLink></div>
      </PaddedCard>
    </main>
  );
}

export function RouteErrorBoundary() {
  const error = useRouteError();
  const message = isRouteErrorResponse(error)
    ? `${error.status} ${error.statusText || 'Route request failed'}`
    : error instanceof Error ? error.message : 'The route could not be loaded.';
  return (
    <main className="route-empty">
      <PaddedCard className="route-empty__card">
        <Badge tone="danger">Route error</Badge>
        <h1>We could not open this page</h1>
        <p>{message}</p>
        <div className="route-empty__actions"><ButtonRouteLink variant="primary" to="/maps">Return to maps</ButtonRouteLink></div>
      </PaddedCard>
    </main>
  );
}
