// Mirror of Colossus.Domain.Tiling.TileSchema — the canonical tile column names and the shared grid
// constant. Kept in lockstep with the C# authority (the client reads columns by these names); the
// tiling conformance test (tiling.test.ts) pins the numeric half against the same fixture the C# tests use.

export const TileColumns = {
  x: 'x',
  y: 'y',
  geometry: 'geometry',
  partOffsets: 'part_offsets',
  triangles: 'triangles',
  id: 'id',
  mergedCount: 'merged_count', // internal LOD tiles only: rows each merged mark stands for
  geom3: 'geom3', // tile format 3: the self-describing encoded-geometry payload (see geometryCodec)
} as const;

/** Grid cells per tile axis — the bake merges sub-pixel marks onto this grid; the client selects tiles
 *  at ≤ this many screen pixels, so one cell ≈ one pixel. Must equal TileSchema.GridPerTile (C#). */
export const GRID_PER_TILE = 512;
