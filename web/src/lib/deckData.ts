import { categoryKey } from './colorScale';
import { columnValue, type TileData } from './tileData';

// A tile's geometry buffers load once; only the color scale changes when you switch measure/scale/theme —
// and that's now GPU state (a LUT texture + uniforms via colorScaleExtension). So the deck binary `data`
// object memoizes per (tile, channel): identity stays stable across camera moves AND recolors, so deck
// re-uploads nothing. The per-mark `getScaleValue` is the only color-related attribute — an integer
// category code or the raw numeric value, sampled through the GPU LUT.
const cache = new WeakMap<TileData, Map<string, object>>();

/** The per-mark value the GPU color LUT reads: canonical category codes (out-of-domain → the unknown
 *  texel at `categories.length`) for a categorical channel, else the raw numeric value (the resident
 *  f32 column by reference — zero copy — when it already is one; NaN marks the unknown/missing color). */
function scaleValues(d: TileData, channel: string, categories: string[] | null): Float32Array {
  const col = d.values[channel];
  const n = d.count;

  if (categories) {
    const codes = new Float32Array(n);
    const miss = categories.length; // the trailing unknown texel (see colorLut.ts)
    const idx = new Map<string, number>();
    categories.forEach((c, i) => idx.set(categoryKey(c), i));
    if (!col) {
      codes.fill(miss);
      return codes;
    }
    if (col instanceof Float32Array || Array.isArray(col)) {
      for (let i = 0; i < n; i++) codes[i] = idx.get(categoryKey(col[i])) ?? miss;
      return codes;
    }
    if (col.kind === 'dict') {
      // Remap the tile-local dict to canonical codes once, then gather — an integer scan, never a
      // per-row string lookup.
      const lut = new Float32Array(col.dict.length);
      for (let c = 0; c < col.dict.length; c++) lut[c] = idx.get(categoryKey(col.dict[c])) ?? miss;
      const cc = col.codes;
      for (let i = 0; i < n; i++) codes[i] = lut[cc[i]];
      return codes;
    }
    for (let i = 0; i < n; i++) codes[i] = idx.get(categoryKey(columnValue(col, i))) ?? miss; // utf8
    return codes;
  }

  if (col instanceof Float32Array) return col; // numeric column by reference — no allocation
  const out = new Float32Array(n);
  if (!col) {
    out.fill(NaN); // missing channel → unknown color via the shader's NaN test
    return out;
  }
  for (let i = 0; i < n; i++) {
    const v = columnValue(col, i);
    out[i] = typeof v === 'number' ? v : Number(v); // non-numeric → NaN → unknown
  }
  return out;
}

/** Spread a per-mark value across each polygon ring's vertices (all a cell's vertices share one value,
 *  so the cell reads flat through the LUT). */
function expandToVertices(perMark: Float32Array, si: Uint32Array, count: number, vertexCount: number): Float32Array {
  const out = new Float32Array(vertexCount);
  for (let p = 0; p < count; p++) {
    const v = perMark[p];
    for (let k = si[p]; k < si[p + 1]; k++) out[k] = v;
  }
  return out;
}

/** Build (or reuse) the deck binary `data` for a tile: geometry + the GPU color value attribute
 *  (`getScaleValue`) + the GPU filter attribute (`getFilterValue`, Phase 1). Cached per (tile, channel);
 *  scale/theme changes never touch this — they only swap the LUT texture on the layer. */
export function tileDeckData(d: TileData, channel: string, categories: string[] | null, filterSize?: number): object {
  let byKey = cache.get(d);
  if (!byKey) {
    byKey = new Map();
    cache.set(d, byKey);
  }
  let data = byKey.get(channel);
  if (!data) {
    const perMark = scaleValues(d, channel, categories);
    // GPU filter slots (Phase 1): read per-vertex/per-mark against uniforms. Stable across recolor.
    const filter = filterSize && d.filterValues ? { getFilterValue: { value: d.filterValues, size: filterSize } } : null;

    if (d.polyPositions) {
      const si = d.polyStartIndices!;
      const attributes: Record<string, object> = {
        getPolygon: { value: d.polyPositions, size: 2 },
        getScaleValue: { value: expandToVertices(perMark, si, d.count, d.vertexCount!), size: 1 },
        ...filter,
      };
      // Bake-time tessellation: an external indices buffer lets deck skip its per-polygon earcut.
      if (d.polyTriangles) attributes.indices = { value: d.polyTriangles, size: 1 };
      data = { length: d.count, startIndices: si, attributes };
    } else {
      data = {
        length: d.count,
        attributes: {
          getPosition: { value: d.positions!, size: 2 },
          getScaleValue: { value: perMark, size: 1 },
          ...filter,
        },
      };
    }
    byKey.set(channel, data);
  }
  return data;
}
