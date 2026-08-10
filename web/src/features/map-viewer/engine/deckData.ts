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

/** Per-mark scale values from a folded measure column (group regime under active context): numeric
 *  measures pass through (NaN → unknown); an argmax column is codes into the category domain already, so
 *  only out-of-range codes (ARGMAX_UNKNOWN, an emptied mark) map to the trailing unknown texel. */
function overrideScaleValues(override: Float32Array | Uint16Array, categories: string[] | null): Float32Array {
  if (!categories) return override instanceof Float32Array ? override : Float32Array.from(override);
  const miss = categories.length;
  const out = new Float32Array(override.length);
  for (let i = 0; i < override.length; i++) out[i] = override[i] < miss ? override[i] : miss;
  return out;
}

/** Build (or reuse) the deck binary `data` for a tile: geometry + the GPU color value attribute
 *  (`getScaleValue`) + the GPU filter attribute (`getFilterValue`, Phase 1). Cached per (tile, channel,
 *  context); scale/theme changes never touch this — they only swap the LUT texture on the layer. In the
 *  group regime an `override` (the folded per-mark measure column) supplies the value attribute instead
 *  of the baked column, keyed by the active context so scrubbing back reuses a cached buffer. */
export function tileDeckData(
  d: TileData,
  channel: string,
  categories: string[] | null,
  filterSize?: number,
  override?: Float32Array | Uint16Array,
  contextKey?: string,
): object {
  let byKey = cache.get(d);
  if (!byKey) {
    byKey = new Map();
    cache.set(d, byKey);
  }
  // The context key applies only when the folded override rides along: a tile whose fold hasn't
  // landed yet falls back to the plain channel entry (baked colours) instead of caching baked values
  // under the context key — the fold's arrival changes the key, which is what hands deck the new buffer.
  // The categories ride in the key because they shape the buffer's VALUES (codes vs raw numbers): a
  // channel rendered once through a mismatched LUT kind (the domain-fetch gap on a colour switch) must
  // not pin its miss-codes under the channel name for the session.
  const catSig = categories ? `c:${categories.join('\u0001')}` : 'n';
  const key = `${override && contextKey ? `${channel}\u0000${contextKey}` : channel}\u0000${catSig}`;
  let data = byKey.get(key);
  if (!data) {
    // A context scrub mints one entry per context visited — cap per tile, oldest first, so a long
    // scrub can't pin an unbounded set of expanded per-vertex buffers.
    if (byKey.size >= 16) byKey.delete(byKey.keys().next().value!);
    const perMark = override ? overrideScaleValues(override, categories) : scaleValues(d, channel, categories);
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
    byKey.set(key, data);
  }
  return data;
}
