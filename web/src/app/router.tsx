import { createBrowserRouter, redirect, type LoaderFunctionArgs } from 'react-router';
import {
  connectionsQuery,
  datasetsQuery,
  managedMapQuery,
  managedMapsQuery,
  sourceAdaptersQuery,
} from '@entities/catalog';
import { canonicalViewerUrl, resolveViewerRoute } from '@features/map-viewer/model/viewRoute';
import { queryClient } from '@shared/query/queryClient';
import { RouteErrorBoundary, RouteFallback, RouteNotFound } from './routes/routeFeedback';

function requiredParam(params: LoaderFunctionArgs['params'], name: string): string {
  const value = params[name];
  if (!value) throw new Response(`Missing route parameter: ${name}`, { status: 400 });
  return value;
}

async function catalogLoader({ request }: LoaderFunctionArgs) {
  const includeArchived = new URL(request.url).searchParams.get('archived') === '1';
  await queryClient.ensureQueryData(managedMapsQuery(includeArchived));
  return null;
}

async function detailLoader({ params }: LoaderFunctionArgs) {
  await queryClient.ensureQueryData(managedMapQuery(requiredParam(params, 'mapId')));
  return null;
}

async function createLoader() {
  await queryClient.ensureQueryData(datasetsQuery());
  return null;
}

async function editLoader({ params }: LoaderFunctionArgs) {
  const mapId = requiredParam(params, 'mapId');
  await Promise.all([
    queryClient.ensureQueryData(datasetsQuery()),
    queryClient.ensureQueryData(managedMapQuery(mapId)),
  ]);
  return null;
}

async function sourcesLoader() {
  await Promise.all([
    queryClient.ensureQueryData(sourceAdaptersQuery()),
    queryClient.ensureQueryData(connectionsQuery()),
    queryClient.ensureQueryData(datasetsQuery()),
  ]);
  return null;
}

function legacyViewerLoader({ request }: LoaderFunctionArgs) {
  const route = resolveViewerRoute(request.url);
  if (!route?.legacy) return null;
  const canonical = new URL(canonicalViewerUrl(request.url, route));
  return redirect(`${canonical.pathname}${canonical.search}${canonical.hash}`);
}

const managementModule = () => import('./routes/managementRoutes');
const viewerModule = () => import('./routes/viewerRoutes');

export const router = createBrowserRouter([
  {
    path: '/sources',
    loader: sourcesLoader,
    lazy: { Component: async () => (await managementModule()).SourcesRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/maps',
    loader: catalogLoader,
    lazy: { Component: async () => (await managementModule()).CatalogRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/maps/new',
    loader: createLoader,
    lazy: { Component: async () => (await managementModule()).CreateMapRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/maps/manage/:mapId/edit',
    loader: editLoader,
    lazy: { Component: async () => (await managementModule()).EditMapRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/maps/manage/:mapId',
    loader: detailLoader,
    lazy: { Component: async () => (await managementModule()).MapDetailRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/maps/:mapId',
    lazy: { Component: async () => (await viewerModule()).MapViewerRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '/embed/:mapId',
    lazy: { Component: async () => (await viewerModule()).EmbedViewerRoute },
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
  {
    path: '*',
    loader: legacyViewerLoader,
    Component: RouteNotFound,
    HydrateFallback: RouteFallback,
    ErrorBoundary: RouteErrorBoundary,
  },
]);
