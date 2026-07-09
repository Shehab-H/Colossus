import type { ColorSpec, ScaleType } from './manifest';
import type { Theme } from './theme';

// Embeddable maps: a view is fully addressable by URL, so an <iframe> reproduces an exact map — the
// view, camera, color-by channel + scale, theme, and any coarse filters. `embed=1` drops the chrome
// (the HUD and the theme toggle) so only the map + legend fill the frame.

export interface EmbedCamera {
  longitude: number;
  latitude: number;
  zoom: number;
}

export interface EmbedParams {
  embed: boolean;
  view: string | null;
  color: string | null;
  colorSpec: Partial<ColorSpec> | null;
  theme: Theme | null;
  camera: EmbedCamera | null;
  filters: Record<string, string>;
}

const SCALES = new Set<ScaleType>(['linear', 'log', 'sqrt', 'diverging', 'quantize', 'quantile', 'threshold', 'ordinal', 'categorical']);

const num = (v: string | null): number | null => {
  if (v === null || v.trim() === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
};

/** A partial color scale override from the URL: pick the scale type and its knobs so one table can be
 *  embedded as a gradient, a binned choropleth, or a diverging map. `color` names the channel. */
function readColorSpec(q: URLSearchParams, channel: string | null): Partial<ColorSpec> | null {
  if (!channel) return null;
  const scale = q.get('scale');
  const bins = num(q.get('bins'));
  const midpoint = num(q.get('midpoint'));
  const scheme = q.get('scheme');
  const spec: Partial<ColorSpec> = { channel };
  if (scale && SCALES.has(scale as ScaleType)) spec.type = scale as ScaleType;
  if (bins !== null) spec.bins = bins;
  if (midpoint !== null) spec.midpoint = midpoint;
  if (scheme) spec.scheme = scheme;
  if (q.get('reverse') === '1') spec.reverse = true;
  // Only an override if something beyond the channel was set.
  return Object.keys(spec).length > 1 ? spec : null;
}

/** Parse the embed-relevant params off the current URL (read once at startup). */
export function readEmbedParams(search = window.location.search): EmbedParams {
  const q = new URLSearchParams(search);
  const lng = num(q.get('lng'));
  const lat = num(q.get('lat'));
  const zoom = num(q.get('z'));
  const theme = q.get('theme');
  const color = q.get('color');
  const filters: Record<string, string> = {};
  for (const [k, v] of q) if (k.startsWith('f_')) filters[k.slice(2)] = v;
  return {
    embed: q.get('embed') === '1',
    view: q.get('view'),
    color,
    colorSpec: readColorSpec(q, color),
    theme: theme === 'dark' || theme === 'light' ? theme : null,
    camera: lng !== null && lat !== null && zoom !== null ? { longitude: lng, latitude: lat, zoom } : null,
    filters,
  };
}

export interface EmbedState {
  view: string;
  color?: string | null;
  theme?: Theme;
  camera?: EmbedCamera | null;
  filters?: Record<string, string>;
}

/** Build a shareable embed URL for a view + its current view state. */
export function buildEmbedUrl(base: string, s: EmbedState): string {
  const url = new URL(base);
  const q = url.searchParams;
  q.set('view', s.view);
  q.set('embed', '1');
  if (s.color) q.set('color', s.color);
  if (s.theme) q.set('theme', s.theme);
  if (s.camera) {
    q.set('lng', s.camera.longitude.toFixed(5));
    q.set('lat', s.camera.latitude.toFixed(5));
    q.set('z', s.camera.zoom.toFixed(2));
  }
  for (const [k, v] of Object.entries(s.filters ?? {})) if (v && v !== '(all)') q.set(`f_${k}`, v);
  return url.toString();
}

/** The <iframe> snippet an embedder pastes into their page. */
export function embedSnippet(url: string, opts: { width?: string; height?: number } = {}): string {
  const width = opts.width ?? '100%';
  const height = opts.height ?? 480;
  return (
    `<iframe src="${url}" width="${width}" height="${height}" ` +
    `style="border:0;border-radius:8px" loading="lazy" title="Colossus map"></iframe>`
  );
}
