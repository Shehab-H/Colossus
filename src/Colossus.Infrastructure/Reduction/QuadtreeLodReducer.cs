using System.Globalization;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.Tiles;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Adaptive quadtree pyramid built with DuckDB as an out-of-core engine (a file-backed
/// database so a bake is bounded by disk, not RAM). A node under budget becomes a leaf holding all its
/// rows; a denser node writes a random reservoir sample and subdivides. Every input row lands in
/// exactly one leaf — nothing dropped. Tiles carry whatever columns staging holds, so geometry,
/// channels, and id flow through untouched.</summary>
public sealed class QuadtreeLodReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.QuadtreeLod;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string parquet = Path.GetFullPath(ctx.StagingParquetPath).Replace('\\', '/');
        string workDir = Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!;
        string dbPath = Path.Combine(workDir, "reduce.duckdb");
        DeleteDatabase(dbPath);

        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        try
        {
            using var conn = new DuckDBConnection($"Data Source={dbPath.Replace('\\', '/')}");
            conn.Open();
            Exec(conn, "SET preserve_insertion_order = false");
            Exec(conn, $"SET temp_directory = '{workDir.Replace('\\', '/')}'");
            Exec(conn, $"CREATE TABLE t AS SELECT * FROM read_parquet('{parquet}')");
            Build(conn, ctx, new TileId(0, 0, 0), tiles, ref leafTotal);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
        return new ReductionResult(tiles, leafTotal);
    }

    private static void Build(DuckDBConnection conn, ReductionContext ctx, TileId tile, List<TileMeta> tiles, ref long leafTotal)
    {
        var (xMin, yMin, xMax, yMax) = TileMath.TileRect(ctx.Root, tile);
        string rect = $"x >= {R(xMin)} AND x < {R(xMax)} AND y >= {R(yMin)} AND y < {R(yMax)}";

        long count = Scalar(conn, $"SELECT count(*) FROM t WHERE {rect}");
        if (count == 0) return;

        bool isLeaf = count <= ctx.TilePointBudget || tile.Z >= ctx.MaxZoom;
        string subset = isLeaf
            ? $"SELECT * FROM t WHERE {rect}"
            : $"SELECT * FROM (SELECT * FROM t WHERE {rect}) USING SAMPLE {ctx.TilePointBudget} ROWS (reservoir)";

        ArrowTiles.Write(conn, subset, Path.Combine(ctx.OutputDirectory, tile.RelativePath));

        long written = isLeaf ? count : Math.Min(count, ctx.TilePointBudget);
        tiles.Add(new TileMeta(tile.Z, tile.X, tile.Y, written, isLeaf));

        if (isLeaf)
        {
            leafTotal += count;
            return;
        }

        for (int q = 0; q < 4; q++)
            Build(conn, ctx, tile.Child(q), tiles, ref leafTotal);
    }

    private static void DeleteDatabase(string dbPath)
    {
        foreach (string p in new[] { dbPath, dbPath + ".wal" })
            if (File.Exists(p)) File.Delete(p);
    }

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

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
