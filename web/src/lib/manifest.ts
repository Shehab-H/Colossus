// Manifest contract — mirrors Colossus.Core.Model (camelCase). The client reads this and obeys it;
// the same code path serves a geo map and a non-geo scatter because only the descriptor differs.

export type Viewport = 'Geo' | 'Orthographic';

export interface Bbox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface TileMeta {
  z: number;
  x: number;
  y: number;
  count: number;
  isLeaf: boolean;
}

export interface ViewDescriptor {
  id: string;
  viewport: Viewport;
  mark: string;
  reduction: string;
}

export interface Manifest {
  version: string;
  view: ViewDescriptor;
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
