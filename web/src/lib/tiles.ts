import { tableFromIPC } from 'apache-arrow';
import { valuesToColors } from './colors';
import type { Bbox, Manifest, TileMeta } from './manifest';
import { tileUrl } from './manifest';

export interface ViewBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  widthPx: number;
}

/** Binary tile payload — typed arrays that go straight into deck.gl binary attributes. */
export interface TileData {
  positions: Float32Array; // interleaved [x0,y0,x1,y1,…]
  colors: Uint8Array; // packed rgb
  length: number;
}

export const tileKey = (z: number, x: number, y: number) => `${z}/${x}/${y}`;

function tileRect(root: Bbox, z: number, x: number, y: number) {
  const n = 2 ** z;
  const cw = (root.maxX - root.minX) / n;
  const ch = (root.maxY - root.minY) / n;
  const xMin = root.minX + x * cw;
  const yMin = root.minY + y * ch;
  return { xMin, yMin, xMax: xMin + cw, yMax: yMin + ch, cw };
}

/**
 * Quadtree LOD + culling: descend from the root, keeping tiles that intersect the viewport and are
 * either leaves or small enough on screen. Zoomed out → internal sample tiles; zoomed in → leaves.
 * Identical for geo and non-geo — it's all data-space math.
 */
export function selectTiles(manifest: Manifest, vb: ViewBounds, targetPx = 640): string[] {
  const index = new Map<string, TileMeta>();
  for (const t of manifest.tiles) index.set(tileKey(t.z, t.x, t.y), t);

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

export async function loadTile(
  viewId: string,
  version: string,
  key: string,
  signal?: AbortSignal,
): Promise<TileData> {
  const [z, x, y] = key.split('/').map(Number);
  const buf = await fetch(tileUrl(viewId, version, z, x, y), { signal }).then((r) => r.arrayBuffer());
  const table = tableFromIPC(new Uint8Array(buf));

  const xs = table.getChild('x')!.toArray() as Float32Array;
  const ys = table.getChild('y')!.toArray() as Float32Array;
  const vs = table.getChild('value')!.toArray() as Float32Array;

  const n = xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  return { positions, colors: valuesToColors(vs), length: n };
}

/** Drop cached tiles that are no longer selected, keeping memory bounded as the viewport moves. */
export function pruneCache(cache: Map<string, TileData>, active: Set<string>, cap = 256) {
  if (cache.size <= cap) return;
  for (const key of cache.keys()) {
    if (cache.size <= cap) break;
    if (!active.has(key)) cache.delete(key);
  }
}
