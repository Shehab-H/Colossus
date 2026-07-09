import type { Theme } from './theme';

// Basemap style for geo (map) views — rendered by MapLibre GL underneath the deck.gl marks, with
// deck.gl driving the shared camera. MapLibre is the token-free renderer that integrates best with
// deck.gl (Mapbox is equivalent but forces a billed token; Google Maps integrates worst).
//
// Defaults are CARTO's free, no-token vector basemaps (a CDN): dark-matter for the dark theme,
// positron for light. Override either with VITE_BASEMAP_STYLE / VITE_BASEMAP_STYLE_LIGHT — e.g.
// point them at a self-hosted style.json for a fully on-prem / offline deployment (R7).
const DARK = 'https://basemaps.cartocdn.com/gl/dark-matter-gl-style/style.json';
const LIGHT = 'https://basemaps.cartocdn.com/gl/positron-gl-style/style.json';

export function basemapStyle(theme: Theme): string {
  const env = import.meta.env as Record<string, string | undefined>;
  return theme === 'light' ? (env.VITE_BASEMAP_STYLE_LIGHT ?? LIGHT) : (env.VITE_BASEMAP_STYLE ?? DARK);
}
