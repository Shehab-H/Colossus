import type { Vector, Table } from 'apache-arrow';

/** Where a decode reads its columns from: one whole-tile Arrow table (the per-tile file) or a set of
 *  per-column blocks (the render pack). Every column read below goes through this, so a single decode
 *  path serves both layouts and the packed path inherits the zero-copy views unchanged. */
export interface ColumnSource {
  numRows: number;
  column(name: string): Vector | null;
}

export const fromTable = (t: Table): ColumnSource => ({ numRows: t.numRows, column: (n) => t.getChild(n) });

/** Flatten a tile's fetched blocks into one column lookup. A group is normally one column of the same
 *  name, but `@geom` carries whatever the mark needs (the geom3 payload, or the x/y pair for points), so
 *  the index is built from each block's actual schema rather than from its group name. */
export function fromBlocks(blocks: Record<string, Table>): ColumnSource {
  const cols = new Map<string, Vector>();
  let numRows = 0;
  for (const t of Object.values(blocks)) {
    numRows = Math.max(numRows, t.numRows);
    for (const f of t.schema.fields) {
      const c = t.getChild(f.name);
      if (c) cols.set(f.name, c);
    }
  }
  return { numRows, column: (n) => cols.get(n) ?? null };
}
