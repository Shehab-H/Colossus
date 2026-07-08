import type { Table } from 'apache-arrow';
import type { Bbox, ChannelSpec, Manifest, TileMeta, ViewConfig } from './manifest';
import { tileUrl } from './manifest';
import { fetchArrowTable } from './arrow';

export interface ViewBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  widthPx: number;
}

/** A loaded tile, read straight from its Arrow buffers. Geometry is built once (stable across camera
 *  moves and measure switches); the raw measure values ride along so recoloring is a client-side scan,
 *  not a re-fetch. Colors are derived at layer time (see App), not stored here. */
export interface TileData {
  count: number; // marks in the tile (polygons or points)
  // Polygon marks: deck SolidPolygonLayer binary layout, mostly zero-copy from Arrow's list buffers.
  polyPositions?: Float32Array; // flat [x0,y0,x1,y1,…], every ring concatenated
  polyStartIndices?: Uint32Array; // vertex offset per polygon; length = count + 1
  vertexCount?: number; // total vertices across all polygons
  polyTriangles?: Uint32Array; // bake-time triangle indices (tile-global) — deck skips earcut entirely
  // Point marks:
  positions?: Float32Array; // interleaved [x0,y0,…]
  // Raw value of every measure channel, one entry per mark — the source for on-demand recoloring.
  values: Record<string, Float32Array>;
}

export const tileKey = (z: number, x: number, y: number) => `${z}/${x}/${y}`;
export const ALL = '(all)';

/** Every measure channel — the choices a choropleth can be colored by. */
export const measureChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'measure');

export const measureChannel = (view: ViewConfig): string =>
  measureChannels(view)[0]?.name ?? 'value';

export const filterableChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'dimension' || c.role === 'temporal');

// A channel as a comparable/displayable string. Temporal values are normalized to YYYY-MM-DD whether
// the tile stored a real DATE or day-integers, so labels, filters, and <input type=date> all agree.
export const channelSqlExpr = (ch: ChannelSpec): string =>
  ch.role === 'temporal'
    ? `strftime(DATE '1970-01-01' + CAST("${ch.name}" AS INTEGER), '%Y-%m-%d')`
    : `CAST("${ch.name}" AS VARCHAR)`;

/** WHERE clause from the active filter selections, using each channel's normalized expression. */
export function buildWhere(view: ViewConfig, filters: Record<string, string>): string {
  const byName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  return Object.entries(filters)
    .filter(([, v]) => v && v !== ALL)
    .map(([name, v]) => {
      const ch = byName.get(name);
      const lhs = ch ? channelSqlExpr(ch) : `CAST("${name}" AS VARCHAR)`;
      return `${lhs} = '${v.replace(/'/g, "''")}'`;
    })
    .join(' AND ');
}

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
 * either leaves or small enough on screen. Identical for every mark and viewport — pure data-space math.
 */
// targetPx matches the bake's 512-cell tile grid, so a merged mark is always ≤1 screen px.
export function selectTiles(manifest: Manifest, vb: ViewBounds, targetPx = 512): string[] {
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

/** Pull every measure column out of the tile into a standalone typed array. We COPY (slice) rather than
 *  hold Arrow's view: a view keeps the whole tile message alive (all columns + Arrow overhead), so
 *  copying only what we need and letting the Table be GC'd is what actually keeps the heap bounded. */
function readMeasures(view: ViewConfig, table: Table): Record<string, Float32Array> {
  const values: Record<string, Float32Array> = {};
  for (const ch of measureChannels(view)) {
    const col = table.getChild(ch.name);
    if (!col) continue;
    const a = col.toArray() as ArrayLike<number>;
    values[ch.name] = a instanceof Float32Array ? a.slice() : Float32Array.from(a);
  }
  return values;
}

/** Extract deck's binary polygon layout from Arrow's `geometry` list column: one contiguous coordinate
 *  buffer + per-polygon vertex offsets. The coordinates are copied out (see readMeasures) so the Arrow
 *  message — including the `x`/`y`/`part_offsets` columns a polygon mark never uses — can be released. */
function readPolygons(table: Table): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild('geometry');
  if (!gv) throw new Error('polygon tile has no geometry column');
  const n = table.numRows;

  if (gv.data.length === 1) {
    const d = gv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array }[] };
    const offsets = d.valueOffsets; // element (float) units into the child buffer
    const base = offsets[0]; // float offset of the first ring (0 for our freshly-written tiles)
    const positions = d.children[0].values.slice(base, offsets[n]); // compact copy of just this tile's coords
    const start = new Uint32Array(n + 1);
    for (let i = 0; i <= n; i++) start[i] = (offsets[i] - base) >> 1; // floats → vertices
    const polyTriangles = readTriangles(table, start);
    return { polyPositions: positions, polyStartIndices: start, vertexCount: positions.length >> 1, count: n, polyTriangles };
  }

  // Fallback (multi-chunk): concatenate ring by ring.
  const pos: number[] = [];
  const start = new Uint32Array(n + 1);
  for (let i = 0; i < n; i++) {
    const sub = gv.get(i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    for (let j = 0; j < flat.length; j++) pos.push(flat[j]);
    start[i + 1] = pos.length >> 1;
  }
  const polyPositions = new Float32Array(pos);
  const polyTriangles = readTriangles(table, start);
  return { polyPositions, polyStartIndices: start, vertexCount: polyPositions.length >> 1, count: n, polyTriangles };
}

/** The bake pre-tessellates every ring (see PolygonTriangulator) into per-row indices; flattening them
 *  to tile-global just adds each row's vertex start — one add per index, no geometry math. Handed to
 *  deck as its external `indices` buffer, which makes it skip the per-polygon earcut that used to run
 *  synchronously on the main thread at every tile load (the stutter that scaled with cell count). */
function readTriangles(table: Table, vertexStart: Uint32Array): Uint32Array | undefined {
  const tv = table.getChild('triangles');
  if (!tv) return undefined; // older bake — deck falls back to tessellating on the client
  const n = table.numRows;

  if (tv.data.length === 1) {
    const d = tv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Int32Array }[] };
    const off = d.valueOffsets;
    const flat = d.children[0].values;
    const out = new Uint32Array(off[n] - off[0]);
    let k = 0;
    for (let i = 0; i < n; i++) {
      const vs = vertexStart[i];
      for (let e = off[i]; e < off[i + 1]; e++) out[k++] = flat[e] + vs;
    }
    return out;
  }

  const acc: number[] = [];
  for (let i = 0; i < n; i++) {
    const sub = tv.get(i);
    const flat = (sub ? sub.toArray() : []) as ArrayLike<number>;
    const vs = vertexStart[i];
    for (let j = 0; j < flat.length; j++) acc.push(flat[j] + vs);
  }
  return Uint32Array.from(acc);
}

export async function loadTile(view: ViewConfig, version: string, key: string, filterSql: string): Promise<TileData> {
  if (filterSql) throw new Error('filtered arrow tiles not implemented — no dimensioned view is in scope');
  const [z, x, y] = key.split('/').map(Number);
  const table = await fetchArrowTable(tileUrl(view.id, version, z, x, y));
  const values = readMeasures(view, table);

  if (view.mark === 'polygon') return { ...readPolygons(table), values };

  const xs = table.getChild('x')!.toArray() as ArrayLike<number>;
  const ys = table.getChild('y')!.toArray() as ArrayLike<number>;
  const n = xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  return { count: n, positions, values };
}

/** The color-ramp range for a measure, read from the root tile's Arrow buffer (no full-store scan).
 *  Per-measure so switching what a choropleth is colored by rescales correctly. */
export async function measureRange(view: ViewConfig, version: string, measure: string): Promise<[number, number]> {
  const t = await fetchArrowTable(tileUrl(view.id, version, 0, 0, 0));
  const vals = t.getChild(measure)?.toArray() as ArrayLike<number> | undefined;
  if (!vals || vals.length === 0) return [0, 1];
  let lo = Infinity;
  let hi = -Infinity;
  for (let i = 0; i < vals.length; i++) {
    const v = vals[i];
    if (v < lo) lo = v;
    if (v > hi) hi = v;
  }
  return [lo, hi];
}

/** Distinct values of each filterable (dimension/temporal) channel, for the filter controls. Read from
 *  the root tile's Arrow buffer. */
export async function discoverOptions(view: ViewConfig, version: string): Promise<Record<string, string[]>> {
  const channels = filterableChannels(view);
  if (channels.length === 0) return {};
  const t = await fetchArrowTable(tileUrl(view.id, version, 0, 0, 0));
  const options: Record<string, string[]> = {};
  for (const ch of channels) {
    const col = t.getChild(ch.name);
    const set = new Set<string>();
    if (col) for (let i = 0; i < t.numRows; i++) set.add(String(col.get(i)));
    options[ch.name] = [...set].sort();
  }
  return options;
}

/** Drop cached tiles no longer selected, keeping memory bounded as the viewport moves. Evicted tiles
 *  re-fetch from the browser's HTTP cache (tiles are immutable), so a tighter cap trades a little
 *  re-decode on backtrack for a much smaller resident heap. */
export function pruneCache(cache: Map<string, TileData>, active: Set<string>, cap = 64) {
  if (cache.size <= cap) return;
  for (const key of cache.keys()) {
    if (cache.size <= cap) break;
    if (!active.has(key)) cache.delete(key);
  }
}
