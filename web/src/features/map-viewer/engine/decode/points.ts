import type { ColumnSource } from './columnSource';
import type { FilterSlots } from '../filters/gpuFilterSlots';
import type { TileColumn, TileData } from './tileData';
import { viewFloat32 } from './columns';
import { TileColumns } from './tileSchema';
import { buildSlotValues } from './filterValues';

/** Point marks from any column source: the representative x/y interleaved into deck's positions buffer.
 *  Identical work whether x/y came from a whole-tile file or the pack's `@geom` block. */
export function decodePoints(
  src: ColumnSource,
  slots: FilterSlots | null | undefined,
  values: Record<string, TileColumn>,
): TileData {
  const xs = viewFloat32(src.column(TileColumns.x)!, TileColumns.x);
  const ys = viewFloat32(src.column(TileColumns.y)!, TileColumns.y);
  const n = src.numRows;
  const positions = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    positions[i * 2] = xs[i];
    positions[i * 2 + 1] = ys[i];
  }
  return { count: n, positions, values, filterValues: buildSlotValues(src, slots, n) };
}
