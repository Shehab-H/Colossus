// Mirrors the baked manifest.json (camelCase, lowercase enums). The client reads the descriptor and
// obeys it — one code path renders every mark and viewport because only the config differs.

export type Viewport = 'geo' | 'orthographic';
export type Mark = 'point' | 'line' | 'arc' | 'rect' | 'polygon' | 'heat' | 'text';

export interface Bbox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface ChannelSpec {
  name: string;
  column: string;
  role: 'measure' | 'dimension' | 'temporal' | 'identity';
  type: string;
}

export interface GeometrySpec {
  kind: string;
}

export interface SourceSpec {
  adapter: string;
  query: string;
  geometry: GeometrySpec;
  channels: ChannelSpec[];
}

/** A data channel bound to a visual channel, optionally with a named scheme (e.g. color ramp). */
export interface ChannelRef {
  channel: string;
  scheme?: string;
}

export type ScaleType =
  | 'linear'
  | 'log'
  | 'sqrt'
  | 'diverging'
  | 'quantize'
  | 'quantile'
  | 'threshold'
  | 'ordinal'
  | 'categorical';

/** How a data channel maps to color. A superset scale spec (à la Vega-Lite): pick the `type`, a named
 *  `scheme` or explicit `range`, and the modifiers that type uses. Everything but `channel` is optional
 *  — the client infers a sensible scale from the channel's datatype when omitted. */
export interface ColorSpec {
  channel: string;
  type?: ScaleType;
  scheme?: string;
  range?: string[]; // explicit hex list — overrides scheme
  domain?: (number | string)[]; // numeric [min,max] or explicit category order
  reverse?: boolean;
  midpoint?: number; // diverging
  bins?: number; // quantize / quantile
  thresholds?: number[]; // threshold
  palette?: Record<string, string>; // categorical: explicit value → hex
  unknown?: string; // color for unmapped / null values
}

export interface EncodingSpec {
  color?: ColorSpec;
  size?: ChannelRef;
}

/** Click-to-inspect config. When absent, marks aren't pickable and clicks do nothing. */
export interface InspectSpec {
  channels: string[];
  title?: string;
}

/** A computed per-mark value (VIEW_CONFIG §4). Its presence opts the view into the group regime. */
export interface MeasureSpec {
  name: string;
  expr: string;
}

export interface ViewConfig {
  id: string;
  title?: string;
  viewport: Viewport;
  mark: Mark;
  reduction?: string;
  source: SourceSpec;
  measures?: MeasureSpec[];
  encoding?: EncodingSpec;
  inspect?: InspectSpec;
}

/** Group regime: the derived split of a view's channels. perMark channels ride the render tiles;
 *  perFact channels live only in the fact companions and drive the fold. */
export interface FactChannels {
  perMark: string[];
  perFact: string[];
}

export interface TileMeta {
  z: number;
  x: number;
  y: number;
  count: number;
  isLeaf: boolean;
}

/** One channel's data domain, scanned from the FULL extract at bake time (unlike the root tile, which
 *  is a sample). Numeric channels carry min/max + a quantile grid; others their distinct values —
 *  `valuesTruncated` marks a capped list, which the client treats as absent. */
export interface ChannelDomain {
  values?: string[];
  valuesTruncated?: boolean;
  min?: number;
  max?: number;
  quantiles?: number[];
}

export interface Manifest {
  version: string;
  /** Tile binary format. 2 = zero-copy (views over one buffer); absent/0/1 = the copy-based decode. */
  tileFormat?: number;
  view: ViewConfig;
  /** The reduction the bake planner chose (e.g. 'aggregate', 'quadtreeLod'). Drives the fidelity label. */
  reduction: string;
  regime: string;
  root: Bbox;
  minZoom: number;
  maxZoom: number;
  tilePointBudget: number;
  totalPoints: number;
  tiles: TileMeta[];
  /** Absent on manifests from older bakes — consumers fall back to scanning the root tile. */
  channelDomains?: Record<string, ChannelDomain>;
  /** Group regime only: the perMark/perFact split (routes a filter to a GPU predicate or the fold). */
  factChannels?: FactChannels;
  /** True when the bake wrote a `z/x/y.facts.arrow` companion beside every tile. */
  companionTiles?: boolean;
  /** The companion grain columns (perFact dict + temporal), the dimensions the fact partials key by. */
  grainChannels?: string[];
  /** Leaf companion packaging (companion-scale R2): leaf companions live as independently compressed
   *  blocks in one per-version archive, range-read per tile. Internal levels stay per-tile files.
   *  Absent selects the per-file layout for every level (older bakes). */
  companionPack?: CompanionPack;
}

/** The leaf companion archive and its directory: `file` is relative to the version directory, `codec`
 *  is a browser-native DecompressionStream format (compression lives inside the archive — a
 *  Content-Encoding wouldn't compose with range requests), and `entries` maps a tile key `z/x/y` to
 *  its `[offset, length]` byte range. */
export interface CompanionPack {
  file: string;
  codec: CompressionFormat;
  entries: Record<string, [number, number]>;
}

const TILES_BASE = import.meta.env.VITE_TILES_BASE ?? 'http://localhost:5174/tiles';
export const API_BASE = import.meta.env.VITE_API_BASE ?? TILES_BASE.replace(/\/tiles\/?$/, '/api');

export function tileUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.arrow`;
}

/** The fact companion beside a group-regime render tile. */
export function factsUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.facts.arrow`;
}

/** The companion pack archive, tagged with the tile whose block a range request is after. Static
 *  servers ignore the query; it makes the URL — and so the service worker's cache key — per-tile. */
export function packBlockUrl(viewId: string, version: string, file: string, key: string): string {
  return `${TILES_BASE}/${viewId}/${version}/${file}?tile=${key}`;
}

/** Resolve latest.json → manifest.json for a view. */
export async function loadManifest(viewId: string): Promise<Manifest> {
  const pointer = await fetch(`${TILES_BASE}/${viewId}/latest.json`, { cache: 'no-cache' }).then((r) => r.json());
  const version: string = pointer.version;
  return fetch(`${TILES_BASE}/${viewId}/${version}/manifest.json`).then((r) => r.json());
}
