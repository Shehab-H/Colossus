// Basemap style for geo (map) views — rendered by MapLibre GL underneath the deck.gl marks, with
// deck.gl driving the shared camera. MapLibre is the token-free renderer that integrates best with
// deck.gl (Mapbox is equivalent but forces a billed token; Google Maps integrates worst).
//
// Default is CARTO's free, no-token dark vector basemap (a CDN). The dark palette keeps the viridis
// marks legible, and the vector style stays crisp to street level. Override with VITE_BASEMAP_STYLE
// — e.g. point it at a self-hosted style.json for a fully on-prem / offline deployment (R7).
export const basemapStyle: string =
  (import.meta.env.VITE_BASEMAP_STYLE as string | undefined) ??
  'https://basemaps.cartocdn.com/gl/dark-matter-gl-style/style.json';
