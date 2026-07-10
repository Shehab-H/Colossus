using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;
using Axis = Colossus.Infrastructure.Tiling.TileSql.Axis;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Adaptive quadtree pyramid built with DuckDB as an out-of-core engine. A node under budget
/// (or with no two rows sharing a ~1px grid cell) becomes a leaf holding all its rows; a denser node
/// keeps one representative row per occupied grid cell — tagged with a <see cref="TileSchema.MergedCount"/>
/// of the rows it stands for — and subdivides. Only sub-pixel overlap is ever folded, so the reduction
/// cannot introduce density steps between neighboring tiles; every input row still lands in exactly one
/// leaf. Tiles carry whatever columns staging holds, so geometry, channels, and id flow through untouched.</summary>
public sealed class QuadtreeLodReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.QuadtreeLod;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        using var db = DuckDbSession.OnDisk(Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!);
        db.Exec($"CREATE TABLE t AS SELECT * FROM read_parquet('{Sql.Path(ctx.StagingParquetPath)}')");
        Build(db, ctx, new TileId(0, 0, 0), tiles, ref leafTotal);
        return new ReductionResult(tiles, leafTotal);
    }

    private static void Build(DuckDbSession db, ReductionContext ctx, TileId tile, List<TileMeta> tiles, ref long leafTotal)
    {
        string rect = TileSql.TileRectPredicate(ctx.Root, tile);

        long count = db.Scalar($"SELECT count(*) FROM t WHERE {rect}");
        if (count == 0) return;

        string gx = TileSql.GridIndex(ctx.Root, tile.Z, Axis.X);
        string gy = TileSql.GridIndex(ctx.Root, tile.Z, Axis.Y);
        bool small = count <= ctx.TilePointBudget || tile.Z >= ctx.MaxZoom;
        long cells = small ? count : db.Scalar($"SELECT count(*) FROM (SELECT DISTINCT {gx}, {gy} FROM t WHERE {rect})");
        bool isLeaf = small || cells == count;

        // A merged node keeps the lowest-rowid row of each occupied grid cell: deterministic, and only
        // rows that would overlap inside one ~1px cell at this zoom are folded — never a visible mark.
        string subset = isLeaf
            ? $"SELECT * FROM t WHERE {rect}"
            : $"""
               WITH g AS (SELECT t.*, {gx} AS __gx, {gy} AS __gy, rowid AS __rid FROM t WHERE {rect})
               SELECT * EXCLUDE (__gx, __gy, __rid),
                      (count(*) OVER (PARTITION BY __gx, __gy))::INTEGER AS {TileSchema.MergedCount}
               FROM g
               QUALIFY row_number() OVER (PARTITION BY __gx, __gy ORDER BY __rid) = 1
               """;

        ArrowTileWriter.Write(db.Connection, subset, Path.Combine(ctx.OutputDirectory, tile.RelativePath),
            ctx.View.DictionaryEncodedChannels());

        tiles.Add(new TileMeta(tile.Z, tile.X, tile.Y, isLeaf ? count : cells, isLeaf));

        if (isLeaf)
        {
            leafTotal += count;
            return;
        }

        for (int q = 0; q < 4; q++)
            Build(db, ctx, tile.Child(q), tiles, ref leafTotal);
    }
}
