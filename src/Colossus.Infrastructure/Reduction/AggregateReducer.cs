using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;
using Axis = Colossus.Infrastructure.Tiling.TileSql.Axis;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Pixel pyramid: a polygon renders as itself while it spans ≥1 screen pixel; anything
/// smaller merges into its single ~1px grid cell (mean per measure). Zoomed out is therefore the
/// zoomed-in picture at screen resolution — LOD is invisible. A tile is a leaf once everything in
/// it is real, so effective resolution is uniform across the viewport at any zoom. All tile/grid math
/// is rendered through <see cref="TileSql"/>; column names come from <see cref="TileSchema"/>.</summary>
public sealed class AggregateReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.Aggregate;

    private const int ZCap = 16;
    // Slack on the ≥1px test so float32 coordinate noise can't split equal-sized polygons across levels.
    private const double ZSlack = 0.02;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string[] measures = Measures(ctx.View);

        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        using var db = DuckDbSession.OnDisk(Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!);
        LoadStaging(db, ctx.StagingParquetPath, ctx.Root);
        BuildPyramid(db, ctx, measures, tiles, ref leafTotal);
        return new ReductionResult(tiles, leafTotal);
    }

    private static string[] Measures(ViewConfig view) => view.Source.Channels
        .Where(c => c.Role == ChannelRole.Measure)
        .Select(c => c.Name)
        .ToArray();

    // Loads staging and tags each row with zreal = the first zoom whose ~1px grid cell the polygon's
    // extent still exceeds, i.e. the level at which it stops being sub-pixel and becomes real.
    private static void LoadStaging(DuckDbSession db, string stagingParquetPath, Bbox root)
    {
        string coordsAt(int parity) =>
            $"list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = {parity})";
        string extent = $"greatest(list_max({coordsAt(1)}) - list_min({coordsAt(1)}), " +
                        $"list_max({coordsAt(0)}) - list_min({coordsAt(0)}))";

        db.Exec($"""
            CREATE TABLE t AS
            WITH s AS (
              SELECT *, {extent} AS ext
              FROM read_parquet('{Sql.Path(stagingParquetPath)}')
            )
            SELECT *, CASE
              WHEN ext IS NULL OR ext <= 0 THEN {ZCap}
              ELSE CAST(greatest(0, least({ZCap}, ceil(log2({Sql.Lit(root.SpanX)} / ({TileSchema.GridPerTile} * ext)) - {Sql.Lit(ZSlack)}))) AS INTEGER)
            END AS zreal
            FROM s
            """);
    }

    private static void BuildPyramid(DuckDbSession db, ReductionContext ctx, string[] measures,
        List<TileMeta> tiles, ref long leafTotal)
    {
        if (db.Scalar("SELECT count(*) FROM t") == 0) return;
        int zMax = (int)db.Scalar("SELECT max(zreal) FROM t");

        db.Exec("CREATE TABLE act (tx BIGINT, ty BIGINT)");
        db.Exec("INSERT INTO act VALUES (0, 0)");

        for (int z = 0; z <= zMax; z++)
        {
            string tx = TileSql.TileIndex(ctx.Root, z, Axis.X);
            string ty = TileSql.TileIndex(ctx.Root, z, Axis.Y);

            db.Exec($"""
                CREATE OR REPLACE TABLE internals AS
                SELECT DISTINCT {tx} AS tx, {ty} AS ty
                FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
                WHERE zreal > {z}
                """);
            var internals = db.LongPairs("SELECT tx, ty FROM internals");

            var written = ArrowTileWriter.WritePartitioned(db.Connection, ContentSql(ctx.Root, z, measures),
                (wtx, wty) => Path.Combine(ctx.OutputDirectory, new TileId(z, (int)wtx, (int)wty).RelativePath),
                ctx.View.DictionaryEncodedChannels());

            foreach (var (wtx, wty, rows) in written)
            {
                bool isLeaf = !internals.Contains((wtx, wty));
                tiles.Add(new TileMeta(z, (int)wtx, (int)wty, rows, isLeaf));
                if (isLeaf) leafTotal += rows;
            }

            if (internals.Count == 0) break;
            db.Exec($"""
                CREATE OR REPLACE TABLE act AS
                SELECT DISTINCT {TileSql.TileIndex(ctx.Root, z + 1, Axis.X)} AS tx,
                                {TileSql.TileIndex(ctx.Root, z + 1, Axis.Y)} AS ty
                FROM t JOIN internals ON {tx} = internals.tx AND {ty} = internals.ty
                """);
        }
    }

    // Rows at this level, partitioned by (tx, ty): real polygons pass through untouched; sub-pixel rows
    // collapse to one synthetic ~1px cell per occupied grid square, averaging each measure.
    private static string ContentSql(Bbox root, int z, string[] measures)
    {
        string tx = TileSql.TileIndex(root, z, Axis.X);
        string ty = TileSql.TileIndex(root, z, Axis.Y);
        string gx = TileSql.GridIndex(root, z, Axis.X);
        string gy = TileSql.GridIndex(root, z, Axis.Y);

        string x0 = TileSql.GridCellMin(root, z, Axis.X, "gx"), x1 = TileSql.GridCellMin(root, z, Axis.X, "gx + 1");
        string y0 = TileSql.GridCellMin(root, z, Axis.Y, "gy"), y1 = TileSql.GridCellMin(root, z, Axis.Y, "gy + 1");
        double halfX = TileSql.GridCellSize(root, z, Axis.X) / 2, halfY = TileSql.GridCellSize(root, z, Axis.Y) / 2;
        string ring = $"[{x0}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y0}::FLOAT]";

        string measReal = string.Concat(measures.Select(m => $", \"{m}\"::FLOAT AS \"{m}\""));
        string measAvg = string.Concat(measures.Select(m => $", avg(\"{m}\")::FLOAT AS \"{m}\""));

        return $"""
            WITH v AS (
              SELECT t.*, {tx} AS tx, {ty} AS ty, {gx} AS gx, {gy} AS gy
              FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
            )
            SELECT tx, ty, {TileSchema.X}::FLOAT AS {TileSchema.X}, {TileSchema.Y}::FLOAT AS {TileSchema.Y},
                   {TileSchema.Geometry}, {TileSchema.PartOffsets}{measReal}
            FROM v WHERE zreal <= {z}
            UNION ALL
            SELECT tx, ty,
                   ({x0} + {Sql.Lit(halfX)})::FLOAT AS {TileSchema.X},
                   ({y0} + {Sql.Lit(halfY)})::FLOAT AS {TileSchema.Y},
                   {ring} AS {TileSchema.Geometry},
                   [0, 5]::INTEGER[] AS {TileSchema.PartOffsets}{measAvg}
            FROM v WHERE zreal > {z}
            GROUP BY tx, ty, gx, gy
            ORDER BY tx, ty
            """;
    }
}
