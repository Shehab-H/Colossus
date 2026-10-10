import type { ViewConfig } from '@/types/viewConfig';
import type { FilterSlots } from '../filters/gpuFilterSlots';
import type { TileData } from './tileData';
import { TileColumns } from './tileSchema';
import { readFields, readFieldsView } from './columns';
import { fromTable, type ColumnSource, fromBlocks } from './columnSource';
import { readPolygons, readPolygonsView } from './polygons';
import { buildSlotValues } from './filterValues';
import { decodePoints } from './points';
import { decodeGeometry } from './geometryCodec';
import type { Table } from 'apache-arrow';

/** Arrow table → TileData. Pure (no fetch) so the decode is unit-testable. The whole tile is decoded
 *  unconditionally — filtering is a GPU uniform now, never a reason to drop rows here. Filter slot
 *  values are built once, per tile, alongside geometry. Format 2 decodes as views over the retained
 *  `buffer` (no column copies); format 1 (older bakes) keeps the copy path below. */
export function decodeTile(
  view: ViewConfig,
  table: Table,
  slots?: FilterSlots | null,
  tileFormat?: number,
  buffer?: ArrayBuffer,
): TileData {
  if ((tileFormat ?? 1) >= 3 && buffer && table.getChild(TileColumns.geom3)) return decodeTileV3(view, table, slots, buffer);
  if ((tileFormat ?? 1) >= 2 && buffer) return decodeTileV2(view, table, slots, buffer);

  const values = readFields(view, table);
  const src = fromTable(table);

  if (view.mark === 'polygon') {
    const poly = readPolygons(table);
    const filterValues = buildSlotValues(src, slots, poly.count, poly.polyStartIndices, poly.vertexCount);
    return { ...poly, values, filterValues };
  }

  const xs = table.getChild(TileColumns.x)!.toArray() as ArrayLike<number>;
  const ys = table.getChild(TileColumns.y)!.toArray() as ArrayLike<number>;
  const n = xs.length;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  const filterValues = buildSlotValues(src, slots, n);
  return { count: n, positions, values, filterValues };
}

/** Format-2 zero-copy decode. The tile is one ArrayBuffer for its whole life; every heavy column is a
 *  typed-array view into it — measures viewed as f32, dict codes reinterpreted in place, geometry and
 *  the tile-global triangle indices viewed with no rebase. Only the small derived arrays are built:
 *  point positions (interleaved x/y), polyStartIndices, per-utf8 offsets, and filterValues. */
function decodeTileV2(view: ViewConfig, table: Table, slots: FilterSlots | null | undefined, buffer: ArrayBuffer): TileData {
  const src = fromTable(table);
  const values = readFieldsView(view, src);

  if (view.mark === 'polygon') {
    const poly = readPolygonsView(table);
    const filterValues = buildSlotValues(src, slots, poly.count, poly.polyStartIndices, poly.vertexCount);
    return { ...poly, values, filterValues, buffer };
  }
  return { ...decodePoints(src, slots, values), buffer };
}

/** Format-3 decode (polygon tiles). Measure/dict columns are viewed exactly as format 2 (zero-copy); the
 *  geometry — dropped from the Arrow columns and carried as one self-describing payload in the geom3 blob —
 *  is synthesized back into the identical polyPositions / polyStartIndices / polyTriangles buffers the
 *  format-2 decode produces (bit-for-bit; see geometryCodec). x/y/id are not present: unused by polygon
 *  marks and re-derivable, so the bake never wrote them. */
function decodeTileV3(view: ViewConfig, table: Table, slots: FilterSlots | null | undefined, buffer: ArrayBuffer): TileData {
  return { ...decodeGeom3(view, fromTable(table), slots), buffer };
}

/** Format-3 area marks from any column source: the encoded payload in the geom3 column synthesized back
 *  into the identical polyPositions / polyStartIndices / polyTriangles buffers (bit-for-bit). */
function decodeGeom3(view: ViewConfig, src: ColumnSource, slots: FilterSlots | null | undefined): TileData {
  const values = readFieldsView(view, src);
  const gv = src.column(TileColumns.geom3);
  if (!gv) throw new Error('format 3: polygon tile has no geom3 column');
  const g = decodeGeometry(gv.get(0) as Uint8Array); // the whole payload lives in row 0
  const count = src.numRows;
  const vertexCount = g.positions.length >> 1;
  return {
    count,
    polyPositions: g.positions,
    polyStartIndices: g.startIndices,
    polyTriangles: g.triangles,
    vertexCount,
    values,
    filterValues: buildSlotValues(src, slots, count, g.startIndices, vertexCount),
  };
}

/** Build a tile from its fetched pack blocks — the packed counterpart of decodeTile. Area marks decode
 *  through geom3, point marks through the x/y pair the `@geom` block carries; either way every column not
 *  in `blocks` is simply absent from `values` until an interaction fetches it. */
export function decodeTileBlocks(
  view: ViewConfig,
  blocks: Record<string, Table>,
  buffers: Record<string, ArrayBuffer>,
  slots?: FilterSlots | null,
): TileData {
  const src = fromBlocks(blocks);
  const tile = src.column(TileColumns.geom3)
    ? decodeGeom3(view, src, slots)
    : decodePoints(src, slots, readFieldsView(view, src));
  return { ...tile, blocks: buffers };
}
