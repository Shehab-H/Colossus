import type { ColorFn } from './colorScale';
import { columnValue, type TileData } from './tileData';

// A tile's geometry buffers are loaded once; only its color changes when you switch the color channel or
// the scale. So we memoize the deck binary `data` object per (tile, channel, scaleKey): identity stays
// stable across camera moves — deck skips re-tessellation, the pan/zoom freeze is gone — and recoloring
// is a client-side scan of already-resident values through the scale, never a re-fetch.
const cache = new WeakMap<TileData, Map<string, object>>();

/** One RGB triplet per mark. Dict columns go through a per-category LUT — an integer gather, never a
 *  per-row string lookup; numeric columns run the scale per mark as before. */
function markColors(d: TileData, channel: string, colorOf: ColorFn): Uint8Array {
  const col = d.values[channel];
  const n = d.count;
  const out = new Uint8Array(n * 3);

  if (!col) {
    const [r, g, b] = colorOf(undefined);
    for (let i = 0; i < n; i++) {
      out[i * 3] = r;
      out[i * 3 + 1] = g;
      out[i * 3 + 2] = b;
    }
    return out;
  }

  if (col instanceof Float32Array || Array.isArray(col)) {
    for (let i = 0; i < n; i++) {
      const [r, g, b] = colorOf(col[i]);
      out[i * 3] = r;
      out[i * 3 + 1] = g;
      out[i * 3 + 2] = b;
    }
    return out;
  }

  if (col.kind === 'dict') {
    const lut = new Uint8Array(col.dict.length * 3);
    for (let c = 0; c < col.dict.length; c++) {
      const [r, g, b] = colorOf(col.dict[c]);
      lut[c * 3] = r;
      lut[c * 3 + 1] = g;
      lut[c * 3 + 2] = b;
    }
    const codes = col.codes;
    for (let i = 0; i < n; i++) {
      const c = codes[i] * 3;
      out[i * 3] = lut[c];
      out[i * 3 + 1] = lut[c + 1];
      out[i * 3 + 2] = lut[c + 2];
    }
    return out;
  }

  // Raw UTF-8 (identity channel picked as color — legal but degenerate): decode per row.
  for (let i = 0; i < n; i++) {
    const [r, g, b] = colorOf(columnValue(col, i));
    out[i * 3] = r;
    out[i * 3 + 1] = g;
    out[i * 3 + 2] = b;
  }
  return out;
}

export function tileDeckData(d: TileData, channel: string, colorOf: ColorFn, scaleKey: string): object {
  let byKey = cache.get(d);
  if (!byKey) {
    byKey = new Map();
    cache.set(d, byKey);
  }
  const cacheKey = `${channel}|${scaleKey}`;
  let data = byKey.get(cacheKey);
  if (!data) {
    const mc = markColors(d, channel, colorOf);

    if (d.polyPositions) {
      // Per-vertex color: each cell's scale color repeated across its ring's vertices.
      const si = d.polyStartIndices!;
      const colors = new Uint8Array(d.vertexCount! * 3);
      for (let p = 0; p < d.count; p++) {
        const r = mc[p * 3];
        const g = mc[p * 3 + 1];
        const b = mc[p * 3 + 2];
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
      data = {
        length: d.count,
        attributes: {
          getPosition: { value: d.positions!, size: 2 },
          getFillColor: { value: mc, size: 3, normalized: true },
        },
      };
    }
    byKey.set(cacheKey, data);
  }
  return data;
}
