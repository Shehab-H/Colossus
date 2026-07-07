using System.Globalization;
using Colossus.Core;
using Colossus.Core.Model;
using Colossus.Core.Reduction;
using DuckDB.NET.Data;

namespace Colossus.Bake;

/// <summary>
/// Builds an adaptive (split-on-overflow) quadtree pyramid from the Hilbert-sorted staging Parquet,
/// using DuckDB as the out-of-core query engine. A node with ≤ budget points becomes a leaf holding
/// ALL of them; a denser node writes a random reservoir SAMPLE (the "random-prefix" tile) and
/// subdivides. Empty regions get no tiles. Every input row lands in exactly one leaf — nothing dropped.
/// </summary>
public sealed class QuadtreeLodReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.QuadtreeLod;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string parquet = Path.GetFullPath(ctx.StagingParquetPath).Replace('\\', '/');

        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        Exec(conn, $"CREATE TABLE t AS SELECT * FROM read_parquet('{parquet}')");

        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        Build(conn, ctx, new TileId(0, 0, 0), tiles, ref leafTotal);
        return new ReductionResult(tiles, leafTotal);
    }

    private void Build(DuckDBConnection conn, ReductionContext ctx, TileId tile, List<TileMeta> tiles, ref long leafTotal)
    {
        var (xMin, yMin, xMax, yMax) = TileMath.TileRect(ctx.Root, tile);
        string rect = Where(xMin, yMin, xMax, yMax);

        long count = Scalar(conn, $"SELECT count(*) FROM t WHERE {rect}");
        if (count == 0) return;

        bool isLeaf = count <= ctx.TilePointBudget || tile.Z >= ctx.MaxZoom;

        // Leaf: all points. Internal: a fair random reservoir sample of size budget.
        string select = $"SELECT CAST(x AS REAL) x, CAST(y AS REAL) y, CAST(value AS REAL) v, CAST(category AS UTINYINT) c FROM ";
        string sql = isLeaf
            ? select + $"t WHERE {rect}"
            : select + $"(SELECT * FROM t WHERE {rect}) USING SAMPLE {ctx.TilePointBudget} ROWS (reservoir)";

        long written = WriteTile(conn, sql, Path.Combine(ctx.OutputDir, tile.RelativePath));
        tiles.Add(new TileMeta(tile.Z, tile.X, tile.Y, written, isLeaf));

        if (isLeaf)
        {
            leafTotal += count;
            return;
        }

        for (int q = 0; q < 4; q++)
            Build(conn, ctx, tile.Child(q), tiles, ref leafTotal);
    }

    private static long WriteTile(DuckDBConnection conn, string sql, string path)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        var vs = new List<float>();
        var cs = new List<byte>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                xs.Add(reader.GetFloat(0));
                ys.Add(reader.GetFloat(1));
                vs.Add(reader.GetFloat(2));
                cs.Add(reader.GetByte(3));
            }
        }

        ArrowIo.WriteTile(path, xs.ToArray(), ys.ToArray(), vs.ToArray(), cs.ToArray(), xs.Count);
        return xs.Count;
    }

    private static string Where(double xMin, double yMin, double xMax, double yMax) =>
        $"x >= {R(xMin)} AND x < {R(xMax)} AND y >= {R(yMin)} AND y < {R(yMax)}";

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static void Exec(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
