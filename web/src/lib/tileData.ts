import type { Table } from 'apache-arrow';
import type { ViewConfig } from './manifest';
import { tileUrl } from './manifest';
import { fetchArrowTable } from './arrow';
import { measureChannels, NUMERIC_TYPES } from './channels';
import { TileColumns } from './schema';

/** A loaded tile, read straight from its Arrow buffers. Geometry is built once (stable across camera
 *  moves and measure switches); the per-mark channel values ride along so recoloring — and answering a
 *  click — is a client-side scan, not a re-fetch. Colors are derived at layer time (see lib/deckData). */
export interface TileData {
  count: number; // marks in the tile (polygons or points)
  // Polygon marks: deck SolidPolygonLayer binary layout, mostly zero-copy from Arrow's list buffers.
  polyPositions?: Float32Array; // flat [x0,y0,x1,y1,…], every ring concatenated
  polyStartIndices?: Uint32Array; // vertex offset per polygon; length = count + 1
  vertexCount?: number; // total vertices across all polygons
  polyTriangles?: Uint32Array; // bake-time triangle indices (tile-global) — deck skips earcut entirely
  // Point marks:
  positions?: Float32Array; // interleaved [x0,y0,…]
  // One value per mark for every measure (numeric — the recoloring source) plus any channel named by
  // `inspect` (may be strings). Keyed by channel name.
  values: Record<string, ArrayLike<number | string>>;
}

export async function loadTile(view: ViewConfig, version: string, key: string, filterSql: string): Promise<TileData> {
  if (filterSql) throw new Error('filtered arrow tiles not implemented — no dimensioned view is in scope');
  const [z, x, y] = key.split('/').map(Number);
  const table = await fetchArrowTable(tileUrl(view.id, version, z, x, y));
  const values = readFields(view, table);

  if (view.mark === 'polygon') return { ...readPolygons(table), values };

  const xs = table.getChild(TileColumns.x)!.toArray() as ArrayLike<number>;
  const ys = table.getChild(TileColumns.y)!.toArray() as ArrayLike<number>;
  const n = xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  return { count: n, positions, values };
}

/** Pull the columns we actually use out of the tile into standalone arrays: every measure (kept numeric
 *  for the color ramp) plus any channel named by `inspect` (materialized as strings if non-numeric). We
 *  COPY rather than hold Arrow's view — a view keeps the whole tile message alive (all columns + Arrow
 *  overhead), so copying only what we need and letting the Table be GC'd is what keeps the heap bounded. */
function readFields(view: ViewConfig, table: Table): Record<string, ArrayLike<number | string>> {
  const specByName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  const names = new Set<string>();
  for (const ch of measureChannels(view)) names.add(ch.name);
  if (view.encoding?.color?.channel) names.add(view.encoding.color.channel); // color may be a dimension
  for (const name of view.inspect?.channels ?? []) names.add(name);
  if (view.inspect?.title) names.add(view.inspect.title);

  const values: Record<string, ArrayLike<number | string>> = {};
  for (const name of names) {
    const col = table.getChild(name);
    if (!col) continue;
    if (NUMERIC_TYPES.has(specByName.get(name)?.type ?? '')) {
      // Numeric column — keep it a typed array (measures need this for the ramp).
      const a = col.toArray() as ArrayLike<number>;
      values[name] = a instanceof Float32Array ? a.slice() : Float32Array.from(a);
    } else {
      // Non-numeric (dict/date) — materialize display strings, one per mark.
      const n = table.numRows;
      const s = new Array<string>(n);
      for (let i = 0; i < n; i++) s[i] = String(col.get(i));
      values[name] = s;
    }
  }
  return values;
}

/** Extract deck's binary polygon layout from Arrow's `geometry` list column: one contiguous coordinate
 *  buffer + per-polygon vertex offsets. The coordinates are copied out (see readMeasures) so the Arrow
 *  message — including the `x`/`y`/`part_offsets` columns a polygon mark never uses — can be released. */
function readPolygons(table: Table): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild(TileColumns.geometry);
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
  const tv = table.getChild(TileColumns.triangles);
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
