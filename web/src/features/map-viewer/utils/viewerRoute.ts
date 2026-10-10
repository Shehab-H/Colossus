export type ViewerSurface = 'map' | 'embed';

export interface ViewerRoute {
  mapId: string;
  surface: ViewerSurface;
  /** True only for the temporary `?view=` compatibility entry point. */
  legacy: boolean;
}

const ROUTE = /^\/(maps|embed)\/([^/]+)\/?$/;

/** Resolve the two public viewer surfaces without pulling a routing framework into the renderer. */
export function resolveViewerRoute(href: string): ViewerRoute | null {
  const url = new URL(href);
  const match = ROUTE.exec(url.pathname);
  if (match) {
    try {
      return {
        mapId: decodeURIComponent(match[2]),
        surface: match[1] === 'embed' ? 'embed' : 'map',
        legacy: false,
      };
    } catch {
      return null;
    }
  }

  const mapId = url.searchParams.get('view');
  if (!mapId) return null;
  return {
    mapId,
    surface: url.searchParams.get('embed') === '1' ? 'embed' : 'map',
    legacy: true,
  };
}

/** Preserve authored viewer state while replacing the legacy selector query with a stable path. */
export function canonicalViewerUrl(href: string, route: ViewerRoute): string {
  const url = new URL(href);
  url.pathname = `/${route.surface === 'embed' ? 'embed' : 'maps'}/${encodeURIComponent(route.mapId)}`;
  url.searchParams.delete('view');
  url.searchParams.delete('embed');
  url.searchParams.delete('controls');
  return url.toString();
}

export interface ViewerUrlState {
  color?: string | null;
  theme?: 'dark' | 'light';
  camera?: { longitude: number; latitude: number; zoom: number } | null;
  filters?: Record<string, string>;
}

/** Replace only state owned by the viewer; debug and scale parameters remain untouched. */
export function urlWithViewerState(href: string, state: ViewerUrlState): string {
  const url = new URL(href);
  if (state.color !== undefined) {
    if (state.color) url.searchParams.set('color', state.color);
    else url.searchParams.delete('color');
  }
  if (state.theme) url.searchParams.set('theme', state.theme);
  if (state.camera) {
    url.searchParams.set('lng', state.camera.longitude.toFixed(5));
    url.searchParams.set('lat', state.camera.latitude.toFixed(5));
    url.searchParams.set('z', state.camera.zoom.toFixed(2));
  }
  if (state.filters) {
    for (const key of [...url.searchParams.keys()]) if (key.startsWith('f_')) url.searchParams.delete(key);
    for (const [name, value] of Object.entries(state.filters)) {
      if (value && value !== '(all)') url.searchParams.set(`f_${name}`, value);
    }
  }
  return url.toString();
}
