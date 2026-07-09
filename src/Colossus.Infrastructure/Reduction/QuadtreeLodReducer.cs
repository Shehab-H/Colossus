using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Adaptive quadtree pyramid built with DuckDB as an out-of-core engine. A node under budget
/// becomes a leaf holding all its rows; a denser node writes a random reservoir sample and subdivides.
/// Every input row lands in exactly one leaf — nothing dropped. Tiles carry whatever columns staging
/// holds, so geometry, channels, and id flow through untouched.</summary>
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

        bool isLeaf = count <= ctx.TilePointBudget || tile.Z >= ctx.MaxZoom;
        string subset = isLeaf
            ? $"SELECT * FROM t WHERE {rect}"
            : $"SELECT * FROM (SELECT * FROM t WHERE {rect}) USING SAMPLE {ctx.TilePointBudget} ROWS (reservoir)";

        ArrowTileWriter.Write(db.Connection, subset, Path.Combine(ctx.OutputDirectory, tile.RelativePath));

        long written = isLeaf ? count : Math.Min(count, ctx.TilePointBudget);
        tiles.Add(new TileMeta(tile.Z, tile.X, tile.Y, written, isLeaf));

        if (isLeaf)
        {
            leafTotal += count;
            return;
        }

        for (int q = 0; q < 4; q++)
            Build(db, ctx, tile.Child(q), tiles, ref leafTotal);
    }
}
