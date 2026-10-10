/** A per-mark channel column in transfer-friendly form. Row-wise JS strings never cross the worker
 *  boundary (cloning ~1M of them per tile was the zoom stutter): categorical columns become integer
 *  codes + a small dictionary, high-cardinality identity columns stay raw UTF-8 decoded per row on
 *  demand (a click), numeric columns stay typed arrays. The string[] form is a last-resort fallback
 *  (multi-chunk or null-bearing high-cardinality columns) that still structured-clones. */
export type DictColumn = { kind: 'dict'; codes: Uint8Array | Uint16Array | Uint32Array; dict: string[] };

export type Utf8Column = { kind: 'utf8'; bytes: Uint8Array; offsets: Int32Array };

export type TileColumn = Float32Array | DictColumn | Utf8Column | string[];

/** A loaded tile, read straight from its Arrow buffers. Geometry is built once (stable across camera
 *  moves, measure switches, and filter changes); the per-mark channel values ride along so recoloring —
 *  and answering a click — is a client-side scan, not a re-fetch. Filtering is GPU-side: `filterValues`
 *  is a per-slot binary attribute the DataFilterExtension tests against uniforms (see filters/gpuFilterSlots).
 *  Colors are derived at layer time (see draw/tileDeckData). */
export interface TileData {
  count: number; // marks in the tile (polygons or points) — resident, pre-filter
  // Polygon marks: deck SolidPolygonLayer binary layout, mostly zero-copy from Arrow's list buffers.
  polyPositions?: Float32Array; // flat [x0,y0,x1,y1,…], every ring concatenated
  polyStartIndices?: Uint32Array; // vertex offset per polygon; length = count + 1
  vertexCount?: number; // total vertices across all polygons
  polyTriangles?: Uint32Array; // bake-time triangle indices (tile-global) — deck skips earcut entirely
  // Point marks:
  positions?: Float32Array; // interleaved [x0,y0,…]
  // GPU filter slots: interleaved [slot0,slot1,…] per mark (points) or per vertex (polygons). Absent
  // when the view has no filterable channels.
  filterValues?: Float32Array;
  // One column per measure plus any channel named by `inspect`, keyed by channel name.
  values: Record<string, TileColumn>;
  // Format 2 only: the single fetched ArrayBuffer every heavy column above is a view into. Held as the
  // retention anchor (and transferred once) so the tile's bytes flow network → worker → GPU with no copy.
  buffer?: ArrayBuffer;
  // Packed tiles (render pack): one inflated buffer per fetched group rather than one per tile, each the
  // retention anchor for the columns viewed out of it. A lazily fetched column adds an entry here; the
  // zero-copy discipline is unchanged, there is simply more than one anchor to keep alive.
  blocks?: Record<string, ArrayBuffer>;
}

const utf8Decoder = new TextDecoder();

/** Read one mark's value out of any column form (the only row-wise string decode left, on click). */
export function columnValue(col: TileColumn | undefined, i: number): number | string | undefined {
  if (!col) return undefined;
  if (col instanceof Float32Array) return col[i];
  if (Array.isArray(col)) return col[i];
  if (col.kind === 'dict') return col.dict[col.codes[i]];
  return utf8Decoder.decode(col.bytes.subarray(col.offsets[i], col.offsets[i + 1]));
}

/** Every ArrayBuffer this tile retains: the format-2 whole-tile anchor, or one per fetched block for a
 *  packed tile. Columns viewed out of any of them are already inside that total. */
function anchors(d: TileData): Set<ArrayBuffer> {
  const keep = new Set<ArrayBuffer>();
  if (d.buffer) keep.add(d.buffer);
  if (d.blocks) for (const b of Object.values(d.blocks)) keep.add(b);
  return keep;
}

/** Approximate resident bytes of a tile — what the cache budgets against (see TileCache). Retained
 *  buffers are counted once each; columns that are views into one of them are already inside that total,
 *  so only separately-built arrays add to it. A packed tile has one anchor per fetched group, so this is
 *  the same accounting generalized from "the buffer" to "the retained set" — no double counting when a
 *  lazily merged column is a view into a block already held. */
export function tileBytes(d: TileData): number {
  const keep = anchors(d);
  let b = 0;
  for (const buf of keep) b += buf.byteLength;
  const own = (v?: ArrayBufferView) => (!v || keep.has(v.buffer as ArrayBuffer) ? 0 : v.byteLength);
  b += own(d.positions) + own(d.polyPositions) + own(d.polyStartIndices) + own(d.polyTriangles) + own(d.filterValues);
  for (const col of Object.values(d.values)) {
    if (col instanceof Float32Array) b += own(col);
    else if (Array.isArray(col)) b += col.length * 24; // rough JS string+slot overhead
    else if (col.kind === 'dict') b += own(col.codes) + col.dict.length * 16;
    else b += own(col.bytes) + own(col.offsets);
  }
  return b;
}
