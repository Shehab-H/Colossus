using System.Globalization;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Infrastructure.Tiles;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Pixel pyramid: a polygon renders as itself while it spans ≥1 screen pixel; anything
/// smaller merges into its single ~1px grid cell (mean per measure). Zoomed out is therefore the
/// zoomed-in picture at screen resolution — LOD is invisible. A tile is a leaf once everything in
/// it is real, so effective resolution is uniform across the viewport at any zoom.</summary>
public sealed class AggregateReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.Aggregate;

    // Grid cells per tile axis; the client selects tiles at ≤ this many screen px, so a cell ≈ 1px.
    private const int GridPerTile = 512;
    private const int ZCap = 16;
    // Slack on the ≥1px test so float32 coordinate noise can't split equal-sized polygons across levels.
    private const double ZSlack = 0.02;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string parquet = Path.GetFullPath(ctx.StagingParquetPath).Replace('\\', '/');
        string workDir = Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!;
        string dbPath = Path.Combine(workDir, "reduce.duckdb");
        DeleteDatabase(dbPath);

        string[] measures = ctx.View.Source.Channels
            .Where(c => c.Role == ChannelRole.Measure)
            .Select(c => c.Name)
            .ToArray();

        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        try
        {
            using var conn = new DuckDBConnection($"Data Source={dbPath.Replace('\\', '/')}");
            conn.Open();
            Exec(conn, "SET preserve_insertion_order = false");
            Exec(conn, $"SET temp_directory = '{workDir.Replace('\\', '/')}'");
            LoadStaging(conn, parquet, ctx.Root);
            BuildPyramid(conn, ctx, measures, tiles, ref leafTotal);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
        return new ReductionResult(tiles, leafTotal);
    }

    // zreal = first level whose grid cell the polygon's extent reaches, i.e. where it becomes real.
    private static void LoadStaging(DuckDBConnection conn, string parquet, Bbox root)
    {
        double span = root.MaxX - root.MinX;
        Exec(conn, $"""
            CREATE TABLE t AS
            WITH s AS (
              SELECT *, greatest(
                list_max(list_filter(geometry, (v, i) -> i % 2 = 1)) - list_min(list_filter(geometry, (v, i) -> i % 2 = 1)),
                list_max(list_filter(geometry, (v, i) -> i % 2 = 0)) - list_min(list_filter(geometry, (v, i) -> i % 2 = 0))
              ) AS ext
              FROM read_parquet('{parquet}')
            )
            SELECT *, CASE
              WHEN ext IS NULL OR ext <= 0 THEN {ZCap}
              ELSE CAST(greatest(0, least({ZCap}, ceil(log2({R(span)} / ({GridPerTile} * ext)) - {R(ZSlack)}))) AS INTEGER)
            END AS zreal
            FROM s
            """);
    }

    private static void BuildPyramid(DuckDBConnection conn, ReductionContext ctx, string[] measures,
        List<TileMeta> tiles, ref long leafTotal)
    {
        if (Scalar(conn, "SELECT count(*) FROM t") == 0) return;
        int zMax = (int)Scalar(conn, "SELECT max(zreal) FROM t");

        Exec(conn, "CREATE TABLE act (tx BIGINT, ty BIGINT)");
        Exec(conn, "INSERT INTO act VALUES (0, 0)");

        for (int z = 0; z <= zMax; z++)
        {
            string tx = TileCoord(ctx.Root, z, "x", ctx.Root.MinX);
            string ty = TileCoord(ctx.Root, z, "y", ctx.Root.MinY);

            Exec(conn, $"""
                CREATE OR REPLACE TABLE internals AS
                SELECT DISTINCT {tx} AS tx, {ty} AS ty
                FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
                WHERE zreal > {z}
                """);
            var internals = ReadTiles(conn, "SELECT tx, ty FROM internals");

            var written = ArrowTiles.WritePartitioned(conn, ContentSql(ctx.Root, z, measures),
                (wtx, wty) => Path.Combine(ctx.OutputDirectory, new TileId(z, (int)wtx, (int)wty).RelativePath));

            foreach (var (wtx, wty, rows) in written)
            {
                bool isLeaf = !internals.Contains((wtx, wty));
                tiles.Add(new TileMeta(z, (int)wtx, (int)wty, rows, isLeaf));
                if (isLeaf) leafTotal += rows;
            }

            if (internals.Count == 0) break;
            string txChild = TileCoord(ctx.Root, z + 1, "x", ctx.Root.MinX);
            string tyChild = TileCoord(ctx.Root, z + 1, "y", ctx.Root.MinY);
            Exec(conn, $"""
                CREATE OR REPLACE TABLE act AS
                SELECT DISTINCT {txChild} AS tx, {tyChild} AS ty
                FROM t JOIN internals ON {tx} = internals.tx AND {ty} = internals.ty
                """);
        }
    }

    // Reals pass through untouched; sub-pixel rows collapse to one row per occupied grid cell.
    private static string ContentSql(Bbox root, int z, string[] measures)
    {
        double span = root.MaxX - root.MinX;
        long nTiles = 1L << z;
        double cell = span / nTiles / GridPerTile;
        long nCells = nTiles * GridPerTile;

        string tx = TileCoord(root, z, "x", root.MinX);
        string ty = TileCoord(root, z, "y", root.MinY);
        string gx = $"CAST(least(floor((x - {R(root.MinX)}) / {R(cell)}), {nCells - 1}) AS BIGINT)";
        string gy = $"CAST(least(floor((y - {R(root.MinY)}) / {R(cell)}), {nCells - 1}) AS BIGINT)";

        string x0 = $"{R(root.MinX)} + gx * {R(cell)}";
        string x1 = $"{R(root.MinX)} + (gx + 1) * {R(cell)}";
        string y0 = $"{R(root.MinY)} + gy * {R(cell)}";
        string y1 = $"{R(root.MinY)} + (gy + 1) * {R(cell)}";
        string ring = $"[({x0})::FLOAT, ({y0})::FLOAT, ({x1})::FLOAT, ({y0})::FLOAT, ({x1})::FLOAT, ({y1})::FLOAT, ({x0})::FLOAT, ({y1})::FLOAT, ({x0})::FLOAT, ({y0})::FLOAT]";

        string measReal = string.Concat(measures.Select(m => $", \"{m}\"::FLOAT AS \"{m}\""));
        string measAvg = string.Concat(measures.Select(m => $", avg(\"{m}\")::FLOAT AS \"{m}\""));

        return $"""
            WITH v AS (
              SELECT t.*, {tx} AS tx, {ty} AS ty, {gx} AS gx, {gy} AS gy
              FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
            )
            SELECT tx, ty, x::FLOAT AS x, y::FLOAT AS y, geometry, part_offsets{measReal}
            FROM v WHERE zreal <= {z}
            UNION ALL
            SELECT tx, ty,
                   (({x0}) + {R(cell / 2)})::FLOAT AS x,
                   (({y0}) + {R(cell / 2)})::FLOAT AS y,
                   {ring} AS geometry,
                   [0, 5]::INTEGER[] AS part_offsets{measAvg}
            FROM v WHERE zreal > {z}
            GROUP BY tx, ty, gx, gy
            ORDER BY tx, ty
            """;
    }

    private static string TileCoord(Bbox root, int z, string col, double min)
    {
        double span = root.MaxX - root.MinX;
        long n = 1L << z;
        return $"CAST(least(floor(({col} - {R(min)}) / {R(span / n)}), {n - 1}) AS BIGINT)";
    }

    private static HashSet<(long, long)> ReadTiles(DuckDBConnection conn, string sql)
    {
        var set = new HashSet<(long, long)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return set;
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
