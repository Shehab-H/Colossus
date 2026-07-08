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

export interface ViewConfig {
  id: string;
  title?: string;
  viewport: Viewport;
  mark: Mark;
  reduction: string;
  source: SourceSpec;
}

export interface TileMeta {
  z: number;
  x: number;
  y: number;
  count: number;
  isLeaf: boolean;
}

export interface Manifest {
  version: string;
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
}

const BASE = import.meta.env.VITE_TILES_BASE ?? 'http://localhost:5174/tiles';

export function tileUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${BASE}/${viewId}/${version}/${z}/${x}/${y}.arrow`;
}

/** Resolve latest.json → manifest.json for a view. */
export async function loadManifest(viewId: string): Promise<Manifest> {
  const pointer = await fetch(`${BASE}/${viewId}/latest.json`, { cache: 'no-cache' }).then((r) => r.json());
  const version: string = pointer.version;
  return fetch(`${BASE}/${viewId}/${version}/manifest.json`).then((r) => r.json());
}
