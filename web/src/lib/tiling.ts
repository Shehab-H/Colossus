import type { Bbox, Manifest, TileMeta } from './manifest';
import { GRID_PER_TILE } from './schema';

/** The data-space rectangle the current camera sees — what tile selection culls against. */
export interface ViewBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  widthPx: number;
}

export const tileKey = (z: number, x: number, y: number) => `${z}/${x}/${y}`;

// The tile lookup is rebuilt on demand but cached per manifest: selectTiles runs on every camera frame,
// and re-mapping every tile each frame is pure waste. Keyed by manifest identity, so a new bake rebuilds.
const indexCache = new WeakMap<Manifest, Map<string, TileMeta>>();
function tileIndex(manifest: Manifest): Map<string, TileMeta> {
  let index = indexCache.get(manifest);
  if (!index) {
    index = new Map();
    for (const t of manifest.tiles) index.set(tileKey(t.z, t.x, t.y), t);
    indexCache.set(manifest, index);
  }
  return index;
}

export function tileRect(root: Bbox, z: number, x: number, y: number) {
  const n = 2 ** z;
  const cw = (root.maxX - root.minX) / n;
  const ch = (root.maxY - root.minY) / n;
  const xMin = root.minX + x * cw;
  const yMin = root.minY + y * ch;
  return { xMin, yMin, xMax: xMin + cw, yMax: yMin + ch, cw };
}

/** Point → tile index at zoom z — the forward map and mirror of the C# tiling authority
 *  (TileMath.PointToTile / TileSql). Pinned to it by tiling.test.ts against the shared fixture. */
export function pointToTile(root: Bbox, z: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const clamp = (v: number) => Math.max(0, Math.min(v, n - 1));
  const x = Math.floor(((px - root.minX) / (root.maxX - root.minX)) * n);
  const y = Math.floor(((py - root.minY) / (root.maxY - root.minY)) * n);
  return [clamp(x), clamp(y)];
}

/**
 * Quadtree LOD + culling: descend from the root, keeping tiles that intersect the viewport and are
 * either leaves or small enough on screen. Identical for every mark and viewport — pure data-space math.
 * `targetPx` matches the bake's grid (GRID_PER_TILE cells per tile), so a merged mark is always ≤1 px.
 */
export function selectTiles(manifest: Manifest, vb: ViewBounds, targetPx = GRID_PER_TILE): string[] {
  const index = tileIndex(manifest);

  const vbSpanX = vb.maxX - vb.minX || 1;
  const chosen: string[] = [];

  const visit = (z: number, x: number, y: number) => {
    const meta = index.get(tileKey(z, x, y));
    if (!meta) return; // empty region — no tile baked here
    const r = tileRect(manifest.root, z, x, y);
    if (r.xMin >= vb.maxX || r.xMax <= vb.minX || r.yMin >= vb.maxY || r.yMax <= vb.minY) return;

    const screenPx = (r.cw / vbSpanX) * vb.widthPx;
    if (meta.isLeaf || screenPx <= targetPx) {
      chosen.push(tileKey(z, x, y));
      return;
    }
    for (let q = 0; q < 4; q++) visit(z + 1, x * 2 + (q & 1), y * 2 + ((q >> 1) & 1));
  };

  visit(0, 0, 0);
  return chosen;
}

/** Tiles likely wanted next, given the current selection: parents (zoom-out is instant), the one-tile
 *  ring at the current level (pan), then the children of the selection (zoom-in). Only tiles the manifest
 *  actually baked, never the current selection, first-seen priority, capped so a prefetch burst can't
 *  flood the loader. Pure — the fetch-locality prefetcher (useTiles) warms these during idle. */
export function prefetchCandidates(manifest: Manifest, selKeys: string[], cap = 12): string[] {
  const present = tileIndex(manifest);
  const out: string[] = [];
  const seen = new Set(selKeys);
  const consider = (z: number, x: number, y: number) => {
    if (z < 0 || x < 0 || y < 0 || out.length >= cap) return;
    const k = tileKey(z, x, y);
    if (seen.has(k) || !present.has(k)) return;
    seen.add(k);
    out.push(k);
  };
  const parsed = selKeys.map((k) => k.split('/').map(Number) as [number, number, number]);
  for (const [z, x, y] of parsed) if (z > 0) consider(z - 1, x >> 1, y >> 1); // parents
  for (const [z, x, y] of parsed)
    for (let dx = -1; dx <= 1; dx++) for (let dy = -1; dy <= 1; dy++) if (dx || dy) consider(z, x + dx, y + dy); // ring
  for (const [z, x, y] of parsed)
    for (let q = 0; q < 4; q++) consider(z + 1, x * 2 + (q & 1), y * 2 + ((q >> 1) & 1)); // children
  return out;
}

/** What to draw while desired tiles load: a missing tile is covered by its nearest loaded ancestor
 *  (zoom-in) or loaded descendants (zoom-out); a quad swaps parent→children only when all four are
 *  loaded. The pyramid makes parent and children pixel-identical at swap size, so refinement is a
 *  single-frame, invisible event. */
export function coverTiles(desired: string[], has: (key: string) => boolean, maxDown = 2): string[] {
  const out = new Set<string>();
  const addDescendants = (z: number, x: number, y: number, depth: number) => {
    for (let q = 0; q < 4; q++) {
      const cz = z + 1;
      const cx = x * 2 + (q & 1);
      const cy = y * 2 + ((q >> 1) & 1);
      if (has(tileKey(cz, cx, cy))) out.add(tileKey(cz, cx, cy));
      else if (depth < maxDown) addDescendants(cz, cx, cy, depth + 1);
    }
  };
  for (const key of desired) {
    if (has(key)) {
      out.add(key);
      continue;
    }
    const [z, x, y] = key.split('/').map(Number);
    let anc = '';
    for (let az = z - 1, ax = x >> 1, ay = y >> 1; az >= 0; az--, ax >>= 1, ay >>= 1) {
      if (has(tileKey(az, ax, ay))) {
        anc = tileKey(az, ax, ay);
        break;
      }
    }
    if (anc) out.add(anc);
    else addDescendants(z, x, y, 1);
  }
  // A tile whose ancestor is also chosen would double-draw — drop it. This is what holds a parent
  // on screen until its whole quad is ready.
  return [...out].filter((k) => {
    let [z, x, y] = k.split('/').map(Number);
    for (z--, x >>= 1, y >>= 1; z >= 0; z--, x >>= 1, y >>= 1) if (out.has(tileKey(z, x, y))) return false;
    return true;
  });
}
