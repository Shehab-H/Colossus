using Colossus.Domain.Measures;
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
        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        using var db = DuckDbSession.OnDisk(Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!);
        LoadTagged(db, "t", ctx.StagingParquetPath, ctx.Root);
        // Facts share each mark's geometry, so a fact's zreal equals its mark's — the merge decision
        // (real vs grid-cell) that the marks pyramid makes and the companion mirrors are the same.
        if (ctx.Companion is { } companion)
            LoadTagged(db, "facts", companion.FactsParquetPath, ctx.Root);
        BuildPyramid(db, ctx, tiles, ref leafTotal);
        return new ReductionResult(tiles, leafTotal);
    }

    private static string[] Measures(ViewConfig view) => view.Source.Channels
        .Where(c => c.Role == ChannelRole.Measure)
        .Select(c => c.Name)
        .ToArray();

    // Group regime only: the dict channels a tile carries alongside geometry — perMark dimensions and
    // argmax measures. Merged sub-pixel cells take the mode; the mark id is handled separately.
    private static string[] DictCarry(ViewConfig view) => view.Source.Channels
        .Where(c => c.Type == ChannelType.Dict && c.Name != TileSchema.Id)
        .Select(c => c.Name)
        .ToArray();

    private static bool HasId(ViewConfig view) => view.Source.Channels.Any(c => c.Name == TileSchema.Id);

    // Loads a parquet and tags each row with zreal = the first zoom whose ~1px grid cell the polygon's
    // extent still exceeds, i.e. the level at which it stops being sub-pixel and becomes real. Used for
    // the marks staging (the pyramid) and, in the group regime, the facts (the companions).
    private static void LoadTagged(DuckDbSession db, string table, string parquetPath, Bbox root)
    {
        string coordsAt(int parity) =>
            $"list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = {parity})";
        string extent = $"greatest(list_max({coordsAt(1)}) - list_min({coordsAt(1)}), " +
                        $"list_max({coordsAt(0)}) - list_min({coordsAt(0)}))";

        db.Exec($"""
            CREATE TABLE {table} AS
            WITH s AS (
              SELECT *, {extent} AS ext
              FROM read_parquet('{Sql.Path(parquetPath)}')
            )
            SELECT *, CASE
              WHEN ext IS NULL OR ext <= 0 THEN {ZCap}
              ELSE CAST(greatest(0, least({ZCap}, ceil(log2({Sql.Lit(root.SpanX)} / ({TileSchema.GridPerTile} * ext)) - {Sql.Lit(ZSlack)}))) AS INTEGER)
            END AS zreal
            FROM s
            """);
    }

    private static void BuildPyramid(DuckDbSession db, ReductionContext ctx,
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

            string contentSql = ContentSql(ctx.Root, z, ctx.View, ctx.GroupRegime);
            Func<long, long, string> tilePath =
                (wtx, wty) => Path.Combine(ctx.OutputDirectory, new TileId(z, (int)wtx, (int)wty).RelativePath);

            List<(long Tx, long Ty, long Rows)> written;
            if (ctx.Companion is { } companion)
            {
                // Materialize the level's content with each mark's row index within its tile, so the
                // companion can carry `mki` — the client's O(1) join to the rendered mark (no string
                // keys). The within-tile order (ORDER BY id) only has to match between the two writes.
                db.Exec($"""
                    CREATE OR REPLACE TABLE content AS
                    SELECT *, (row_number() OVER (PARTITION BY tx, ty ORDER BY "{TileSchema.Id}") - 1)::INTEGER AS mki
                    FROM ({contentSql})
                    """);
                written = ArrowTileWriter.WritePartitioned(db.Connection,
                    "SELECT * EXCLUDE (mki) FROM content ORDER BY tx, ty, mki", tilePath,
                    ctx.View.DictionaryEncodedChannels(), ctx.CanonicalDictionaryOrders);

                // Companions ride the same active tiles (facts share their marks' (tx,ty)), so each
                // render tile gets a z/x/y.facts.arrow with its facts' partials at grain, keyed by mki.
                ArrowTileWriter.WritePartitioned(db.Connection, CompanionSql(ctx.Root, z, companion),
                    (wtx, wty) => Path.Combine(ctx.OutputDirectory, $"{z}/{wtx}/{wty}.facts.arrow"),
                    companion.GrainChannels.Where(c => c.Type == ChannelType.Dict).Select(c => c.Name).ToHashSet(),
                    companion.CanonicalDictionaryOrders);
            }
            else
            {
                written = ArrowTileWriter.WritePartitioned(db.Connection, contentSql, tilePath,
                    ctx.View.DictionaryEncodedChannels(), ctx.CanonicalDictionaryOrders);
            }

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
    // collapse to one synthetic ~1px cell per occupied grid square, averaging each measure. In the group
    // regime the mark id and dict channels ride along too — a merged cell takes the grid key as its id
    // (matching its fact companion, MarkKey) and the mode of each dict channel.
    private static string ContentSql(Bbox root, int z, ViewConfig view, bool groupRegime)
    {
        string tx = TileSql.TileIndex(root, z, Axis.X);
        string ty = TileSql.TileIndex(root, z, Axis.Y);
        string gx = TileSql.GridIndex(root, z, Axis.X);
        string gy = TileSql.GridIndex(root, z, Axis.Y);

        string x0 = TileSql.GridCellMin(root, z, Axis.X, "gx"), x1 = TileSql.GridCellMin(root, z, Axis.X, "gx + 1");
        string y0 = TileSql.GridCellMin(root, z, Axis.Y, "gy"), y1 = TileSql.GridCellMin(root, z, Axis.Y, "gy + 1");
        double halfX = TileSql.GridCellSize(root, z, Axis.X) / 2, halfY = TileSql.GridCellSize(root, z, Axis.Y) / 2;
        string ring = $"[{x0}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y0}::FLOAT]";

        string[] measures = Measures(view);
        // f32 with null → NaN so the tile buffer is view-safe on the client (format 2): no null bitmap,
        // no cast. avg() of an all-null group would otherwise be NULL.
        string measReal = string.Concat(measures.Select(m => $", COALESCE(\"{m}\"::FLOAT, 'nan'::FLOAT) AS \"{m}\""));
        string measAvg = string.Concat(measures.Select(m => $", COALESCE(avg(\"{m}\")::FLOAT, 'nan'::FLOAT) AS \"{m}\""));

        string idReal = "", idMerged = "", dictReal = "", dictMerged = "";
        if (groupRegime)
        {
            if (HasId(view))
            {
                idReal = $", \"{TileSchema.Id}\"";
                idMerged = $", {MarkKey.MergedSql("gx", "gy")} AS \"{TileSchema.Id}\"";
            }
            string[] dicts = DictCarry(view);
            dictReal = string.Concat(dicts.Select(d => $", \"{d}\""));
            dictMerged = string.Concat(dicts.Select(d => $", mode(\"{d}\") AS \"{d}\""));
        }

        return $"""
            WITH v AS (
              SELECT t.*, {tx} AS tx, {ty} AS ty, {gx} AS gx, {gy} AS gy
              FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
            )
            SELECT tx, ty, {TileSchema.X}::FLOAT AS {TileSchema.X}, {TileSchema.Y}::FLOAT AS {TileSchema.Y},
                   {TileSchema.Geometry}, {TileSchema.PartOffsets}{idReal}{measReal}{dictReal}
            FROM v WHERE zreal <= {z}
            UNION ALL
            SELECT tx, ty,
                   ({x0} + {Sql.Lit(halfX)})::FLOAT AS {TileSchema.X},
                   ({y0} + {Sql.Lit(halfY)})::FLOAT AS {TileSchema.Y},
                   {ring} AS {TileSchema.Geometry},
                   [0, 5]::INTEGER[] AS {TileSchema.PartOffsets}{idMerged}{measAvg}{dictMerged}
            FROM v WHERE zreal > {z}
            GROUP BY tx, ty, gx, gy
            ORDER BY tx, ty
            """;
    }

    // Fact partials at grain for this level, partitioned by (tx, ty). Each fact keys to its mark — the
    // real geometry key while the mark is real (zreal ≤ z), else the grid-cell key it merged into —
    // then joins the materialized content to carry `mki`, the mark's row index within its render tile.
    // The client fold is then an integer gather (out[mki[row]] += partial[row]), never a string join.
    private static string CompanionSql(Bbox root, int z, CompanionSpec c)
    {
        string tx = TileSql.TileIndex(root, z, Axis.X);
        string ty = TileSql.TileIndex(root, z, Axis.Y);
        string gx = TileSql.GridIndex(root, z, Axis.X);
        string gy = TileSql.GridIndex(root, z, Axis.Y);
        string mk = $"CASE WHEN zreal <= {z} THEN {MarkKey.RealSql()} ELSE {MarkKey.MergedSql("gx", "gy")} END";
        string grain = string.Concat(c.GrainChannels.Select(ch => $", \"{ch.Name}\""));
        string grainOut = string.Concat(c.GrainChannels.Select(ch => $", g.\"{ch.Name}\""));
        string partials = string.Concat(c.Partials.Select(p => $", {PartialSql(p)} AS \"{p.Name}\""));
        string partialsOut = string.Concat(c.Partials.Select(p => $", g.\"{p.Name}\""));

        return $"""
            WITH vf AS (
              SELECT facts.*, {gx} AS gx, {gy} AS gy, {tx} AS tx, {ty} AS ty
              FROM facts JOIN act ON {tx} = act.tx AND {ty} = act.ty
            ),
            g AS (
              SELECT tx, ty, ({mk}) AS mk{grain}{partials}
              FROM vf
              GROUP BY ALL
            )
            SELECT g.tx, g.ty, content.mki{grainOut}{partialsOut}
            FROM g JOIN content ON content.tx = g.tx AND content.ty = g.ty AND content."{TileSchema.Id}" = g.mk
            ORDER BY g.tx, g.ty, content.mki
            """;
    }

    private static string PartialSql(Partial p) => p.Kind switch
    {
        PartialKind.Sum => $"COALESCE(sum(\"{p.Channel}\"), 0)::FLOAT",
        PartialKind.Count => "count(*)::INTEGER",
        PartialKind.Swp => $"COALESCE(sum(\"{p.Channel}\" * \"{p.Weight}\"), 0)::FLOAT",
        PartialKind.Min => $"min(\"{p.Channel}\")::FLOAT",
        PartialKind.Max => $"max(\"{p.Channel}\")::FLOAT",
        _ => throw new InvalidOperationException($"unhandled partial {p.Kind}"),
    };
}
