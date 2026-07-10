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
        var (min, max) = AxisBounds(root, axis);
        return EdgeSql(min, CellSize(min, max, (1L << z) * gridPerTile), cellExpr);
    }

    public static double GridCellSize(Bbox root, int z, Axis axis, int gridPerTile = TileSchema.GridPerTile)
    {
        var (min, max) = AxisBounds(root, axis);
        return CellSize(min, max, (1L << z) * gridPerTile);
    }

    /// <summary>The quadtree rectangle predicate for a tile — the SQL mirror of
    /// <see cref="TileMath.Contains"/>: half-open on interior seams, closed on the root's own max edges.</summary>
    public static string TileRectPredicate(Bbox root, TileId tile)
    {
        long n = 1L << tile.Z;
        var (xMin, yMin, xMax, yMax) = TileMath.TileRect(root, tile);
        string xHi = tile.X == n - 1 ? "<=" : "<";
        string yHi = tile.Y == n - 1 ? "<=" : "<";
        return $"{TileSchema.X} >= {Sql.Dbl(xMin)} AND {TileSchema.X} {xHi} {Sql.Dbl(xMax)} AND " +
               $"{TileSchema.Y} >= {Sql.Dbl(yMin)} AND {TileSchema.Y} {yHi} {Sql.Dbl(yMax)}";
    }

    /// <summary>Mirror of <see cref="TileMath.CellIndex"/>: the same float estimate in the same operand
    /// order, then the same one-cell correction against <see cref="TileMath.Edge"/>. Without the correction
    /// a point on a seam indexes into one cell but falls inside the neighbour's rect.</summary>
    private static string CellIndex(Bbox root, Axis axis, long n)
    {
        var (min, max) = AxisBounds(root, axis);
        string col = axis == Axis.X ? TileSchema.X : TileSchema.Y;
        double cell = CellSize(min, max, n);

        string raw = $"CAST(greatest(0, least(floor(({col} - {Sql.Dbl(min)}) / {Sql.Dbl(max - min)} * {n}), {n - 1})) AS BIGINT)";
        return $"CASE WHEN {raw} > 0 AND {col} < {EdgeSql(min, cell, raw)} THEN {raw} - 1 " +
               $"WHEN {raw} < {n - 1} AND {col} >= {EdgeSql(min, cell, $"{raw} + 1")} THEN {raw} + 1 " +
               $"ELSE {raw} END";
    }

    private static string EdgeSql(double min, double cell, string indexExpr) =>
        $"({Sql.Dbl(min)} + ({indexExpr}) * {Sql.Dbl(cell)})";

    private static double CellSize(double min, double max, long n) => (max - min) / n;

    private static (double Min, double Max) AxisBounds(Bbox root, Axis axis) =>
        axis == Axis.X ? (root.MinX, root.MaxX) : (root.MinY, root.MaxY);
}
