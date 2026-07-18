namespace Colossus.Domain.Tiling;

/// <summary>The canonical tile schema (RULES R3): the column names every tile carries, regardless of
/// source or mark, plus the shared tiling constants. This is the single authority — the ClickHouse
/// extract emits these names, the Arrow writer special-cases them, and the client reads them. They are
/// referenced through here, never spelled as string literals, so a rename is one edit and a compile
/// error rather than a silent runtime mismatch downstream.</summary>
public static class TileSchema
{
    /// <summary>Representative point X (the point itself, or a shape's centroid). Drives sort/LOD/query.</summary>
    public const string X = "x";

    /// <summary>Representative point Y.</summary>
    public const string Y = "y";

    /// <summary>Flat interleaved vertex coords for non-point marks; absent for points.</summary>
    public const string Geometry = "geometry";

    /// <summary>Ring/part start indices delimiting parts within <see cref="Geometry"/>.</summary>
    public const string PartOffsets = "part_offsets";

    /// <summary>Bake-time triangle indices, tile-global (each row's indices rebased by its vertex start)
    /// so the client takes one view over the whole buffer and never tessellates.</summary>
    public const string Triangles = "triangles";

    /// <summary>Optional source identity for tooltips / client-side joins.</summary>
    public const string Id = "id";

    /// <summary>How many source rows a merged LOD mark stands for (quadtree internal tiles only;
    /// absent on leaf tiles, where every mark is one row).</summary>
    public const string MergedCount = "merged_count";

    /// <summary>Tile format 3 (RULES R3): the single self-describing binary geometry payload carried by a
    /// polygon tile in place of <see cref="Geometry"/>/<see cref="PartOffsets"/>/<see cref="Triangles"/>/
    /// <see cref="X"/>/<see cref="Y"/>/<see cref="Id"/>. Held in row 0 of a one-row-populated binary column
    /// (the rest empty); the client decodes it into the exact format-2 buffers. Measure/dict columns stay
    /// as-is (single-chunk, non-null, zero-copy). See <c>GeometryCodec</c> for the payload layout.</summary>
    public const string Geom3 = "geom3";

    /// <summary>Grid cells per tile axis. The bake merges sub-pixel marks onto this grid and the client
    /// selects tiles at ≤ this many screen pixels, so one grid cell ≈ one screen pixel. The bake and the
    /// client must use the same value; it lives here so they share one definition.</summary>
    public const int GridPerTile = 512;
}
