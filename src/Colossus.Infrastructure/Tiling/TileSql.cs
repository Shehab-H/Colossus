using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.Tiling;

/// <summary>The one place the tiling scheme is rendered as DuckDB SQL. The geometric authority is
/// <see cref="TileMath"/> (C#); this projects the same math into SQL expressions the reducers run
/// server-side. A DuckDB-backed conformance test pins <c>TileSql ≡ TileMath</c>, so the two never drift.
/// Column names come from <see cref="TileSchema"/>, never string literals.</summary>
public static class TileSql
{
    public enum Axis { X, Y }

    /// <summary>SQL for the tile index of the canonical x/y column at zoom <paramref name="z"/> — the
    /// mirror of <see cref="TileMath.PointToTile"/> for one axis.</summary>
    public static string TileIndex(Bbox root, int z, Axis axis) => CellIndex(root, axis, 1L << z);

    /// <summary>SQL for the sub-tile grid-cell index at zoom <paramref name="z"/> (that is,
    /// <c>2^z · gridPerTile</c> divisions across the root) — the ~1px merge grid the aggregate uses.</summary>
    public static string GridIndex(Bbox root, int z, Axis axis, int gridPerTile = TileSchema.GridPerTile) =>
        CellIndex(root, axis, (1L << z) * gridPerTile);

    /// <summary>Lower/upper data-space edge of grid cell <paramref name="cellExpr"/> along an axis, at the
    /// same <paramref name="z"/>/<paramref name="gridPerTile"/> resolution as <see cref="GridIndex"/>.</summary>
    public static string GridCellMin(Bbox root, int z, Axis axis, string cellExpr, int gridPerTile = TileSchema.GridPerTile)
    {
        var (min, span) = AxisBounds(root, axis);
        double cell = span / ((1L << z) * gridPerTile);
        return $"({Sql.Lit(min)} + ({cellExpr}) * {Sql.Lit(cell)})";
    }

    public static double GridCellSize(Bbox root, int z, Axis axis, int gridPerTile = TileSchema.GridPerTile)
    {
        var (_, span) = AxisBounds(root, axis);
        return span / ((1L << z) * gridPerTile);
    }

    /// <summary>The quadtree rectangle predicate for a tile, built from <see cref="TileMath.TileRect"/>
    /// (half-open on the max edges, matching the C# authority).</summary>
    public static string TileRectPredicate(Bbox root, TileId tile)
    {
        var (xMin, yMin, xMax, yMax) = TileMath.TileRect(root, tile);
        return $"{TileSchema.X} >= {Sql.Lit(xMin)} AND {TileSchema.X} < {Sql.Lit(xMax)} AND " +
               $"{TileSchema.Y} >= {Sql.Lit(yMin)} AND {TileSchema.Y} < {Sql.Lit(yMax)}";
    }

    private static string CellIndex(Bbox root, Axis axis, long n)
    {
        var (min, span) = AxisBounds(root, axis);
        string col = axis == Axis.X ? TileSchema.X : TileSchema.Y;
        // floor then clamp to [0, n-1] — exactly TileMath.PointToTile, in the same operand order so the
        // float result is bit-identical.
        return $"CAST(greatest(0, least(floor(({col} - {Sql.Lit(min)}) / {Sql.Lit(span)} * {n}), {n - 1})) AS BIGINT)";
    }

    private static (double Min, double Span) AxisBounds(Bbox root, Axis axis) =>
        axis == Axis.X ? (root.MinX, root.SpanX) : (root.MinY, root.SpanY);
}
