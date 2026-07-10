using Colossus.Domain.Model;

namespace Colossus.Domain.Tiling;

/// <summary>Maps between data-space coordinates and quadtree tiles over a padded, square bbox.
/// Y grows upward — we own bake and client, so we keep one convention rather than the TMS/XYZ flip.</summary>
public static class TileMath
{
    /// <summary>Data-space coordinate of grid line <paramref name="i"/> of <paramref name="n"/> equal
    /// divisions of <c>[min, max]</c>. Every tile and grid boundary comes from here.
    ///
    /// <para><paramref name="n"/> is always a power of two, so <c>(max - min) / n</c> is exact and the
    /// rounding of <c>min + i * cell</c> is a function of the real number <c>i * (max - min) / n</c> alone.
    /// Two boundaries naming the same line therefore land on the same double, bit for bit: a tile's max
    /// edge is its neighbour's min edge, and <c>Edge(n, i) == Edge(n &lt;&lt; k, i &lt;&lt; k)</c> at every
    /// depth. Deriving a max edge as <c>edgeMin + cell</c> rounds twice and breaks this, which lets a point
    /// on a seam satisfy two adjacent tile rects at once — or neither.</para>
    ///
    /// <para>The last line snaps to <paramref name="max"/> so the tiles cover the root exactly.</para></summary>
    public static double Edge(double min, double max, long n, long i) =>
        i >= n ? max : min + i * ((max - min) / n);

    /// <summary>Index of the cell of <paramref name="n"/> divisions holding <paramref name="p"/>, under the
    /// half-open rule <c>Edge(i) &lt;= p &lt; Edge(i + 1)</c> — the exact inverse of <see cref="Edge"/>.
    /// The float estimate can be off by one where a rounded edge falls on the far side of the true division
    /// boundary, so it is corrected against the edges themselves; one cell is always enough.
    /// <c>TileSql.TileIndex</c> projects this into SQL.</summary>
    public static long CellIndex(double min, double max, long n, double p)
    {
        long i = Math.Clamp((long)Math.Floor((p - min) / (max - min) * n), 0, n - 1);
        if (i > 0 && p < Edge(min, max, n, i)) return i - 1;
        if (i < n - 1 && p >= Edge(min, max, n, i + 1)) return i + 1;
        return i;
    }

    public static (double XMin, double YMin, double XMax, double YMax) TileRect(Bbox root, TileId tile)
    {
        long n = 1L << tile.Z;
        return (Edge(root.MinX, root.MaxX, n, tile.X), Edge(root.MinY, root.MaxY, n, tile.Y),
                Edge(root.MinX, root.MaxX, n, tile.X + 1), Edge(root.MinY, root.MaxY, n, tile.Y + 1));
    }

    /// <summary>Whether <paramref name="tile"/> owns the point. Interior seams are half-open, so no two
    /// tiles share a point; the root's own max edges are closed, so every point of the root box lands in
    /// exactly one tile at each depth. <c>TileSql.TileRectPredicate</c> projects this into SQL.</summary>
    public static bool Contains(Bbox root, TileId tile, double px, double py)
    {
        long n = 1L << tile.Z;
        var (xMin, yMin, xMax, yMax) = TileRect(root, tile);
        bool inX = px >= xMin && (tile.X == n - 1 ? px <= xMax : px < xMax);
        bool inY = py >= yMin && (tile.Y == n - 1 ? py <= yMax : py < yMax);
        return inX && inY;
    }

    public static (int X, int Y) PointToTile(Bbox root, int z, double px, double py)
    {
        long n = 1L << z;
        return ((int)CellIndex(root.MinX, root.MaxX, n, px), (int)CellIndex(root.MinY, root.MaxY, n, py));
    }
}
