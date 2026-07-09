import type { ColorFn } from './colorScale';
import type { TileData } from './tileData';

// A tile's geometry buffers are loaded once; only its color changes when you switch the color channel or
// the scale. So we memoize the deck binary `data` object per (tile, channel, scaleKey): identity stays
// stable across camera moves — deck skips re-tessellation, the pan/zoom freeze is gone — and recoloring
// is a client-side scan of already-resident values through the scale, never a re-fetch.
const cache = new WeakMap<TileData, Map<string, object>>();

export function tileDeckData(d: TileData, channel: string, colorOf: ColorFn, scaleKey: string): object {
  let byKey = cache.get(d);
  if (!byKey) {
    byKey = new Map();
    cache.set(d, byKey);
  }
  const cacheKey = `${channel}|${scaleKey}`;
  let data = byKey.get(cacheKey);
  if (!data) {
    const vals = d.values[channel] as ArrayLike<number | string> | undefined;
    const valueAt = (i: number) => (vals ? vals[i] : undefined);

    if (d.polyPositions) {
      // Per-vertex color: each cell's scale color repeated across its ring's vertices.
      const si = d.polyStartIndices!;
      const colors = new Uint8Array(d.vertexCount! * 3);
      for (let p = 0; p < d.count; p++) {
        const [r, g, b] = colorOf(valueAt(p));
        for (let v = si[p]; v < si[p + 1]; v++) {
          colors[v * 3] = r;
          colors[v * 3 + 1] = g;
          colors[v * 3 + 2] = b;
        }
      }
      const attributes: Record<string, object> = {
        getPolygon: { value: d.polyPositions, size: 2 },
        getFillColor: { value: colors, size: 3, normalized: true },
      };
      // Bake-time tessellation: with an external indices buffer deck skips its per-polygon earcut —
      // the synchronous main-thread block that made stutter scale with cell count.
      if (d.polyTriangles) attributes.indices = { value: d.polyTriangles, size: 1 };
      data = { length: d.count, startIndices: si, attributes };
    } else {
      const colors = new Uint8Array(d.count * 3);
      for (let i = 0; i < d.count; i++) {
        const [r, g, b] = colorOf(valueAt(i));
        colors[i * 3] = r;
        colors[i * 3 + 1] = g;
        colors[i * 3 + 2] = b;
      }
      data = {
        length: d.count,
        attributes: {
          getPosition: { value: d.positions!, size: 2 },
          getFillColor: { value: colors, size: 3, normalized: true },
        },
      };
    }
    byKey.set(cacheKey, data);
  }
  return data;
}
