import type { TileData } from './tileData';
import { TileColumns } from './tileSchema';
import type { Table } from 'apache-arrow';

/** Extract deck's binary polygon layout from Arrow's `geometry` list column: one contiguous coordinate
 *  buffer + per-polygon vertex offsets. The coordinates are copied out (see readFields) so the Arrow
 *  message — including the `x`/`y`/`part_offsets` columns a polygon mark never uses — can be released. */
export function readPolygons(
  table: Table,
): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
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

/** Format-2 polygon geometry as views over the tile buffer: the coordinate child buffer viewed whole,
 *  the tile-global triangle indices reinterpreted in place (no per-row rebase — the bake already did it),
 *  and only the small (n+1) polyStartIndices built. */
export function readPolygonsView(
  table: Table,
): Pick<TileData, 'polyPositions' | 'polyStartIndices' | 'vertexCount' | 'count' | 'polyTriangles'> {
  const gv = table.getChild(TileColumns.geometry);
  if (!gv) throw new Error('polygon tile has no geometry column');
  const n = table.numRows;
  const d = gv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array }[] };
  const offsets = d.valueOffsets; // float units into the child buffer
  if (offsets[0] !== 0) throw new Error('format 2: geometry child buffer is not zero-based');
  const polyPositions = d.children[0].values.subarray(0, offsets[n]); // view over this tile's coords
  const start = new Uint32Array(n + 1);
  for (let i = 0; i <= n; i++) start[i] = offsets[i] >> 1; // floats → vertices
  const polyTriangles = readTrianglesView(table);
  return { polyPositions, polyStartIndices: start, vertexCount: polyPositions.length >> 1, count: n, polyTriangles };
}

/** Format-2 triangle indices: one Uint32 view reinterpreting the Int32 child buffer (indices < 2^31,
 *  identical bytes). Already tile-global from the bake, so there is no rebase loop. */
function readTrianglesView(table: Table): Uint32Array | undefined {
  const tv = table.getChild(TileColumns.triangles);
  if (!tv) return undefined;
  const n = table.numRows;
  const d = tv.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Int32Array }[] };
  const off = d.valueOffsets;
  const flat = d.children[0].values;
  return new Uint32Array(flat.buffer, flat.byteOffset + off[0] * 4, off[n] - off[0]);
}
