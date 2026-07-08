using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Infrastructure.Tiles;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Ships every row as a single root tile — used when the source is already under budget
/// (typically because the view's query aggregated it). Σ leaf rows == source.</summary>
public sealed class RawPassthroughReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.RawPassthrough;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string parquet = Path.GetFullPath(ctx.StagingParquetPath).Replace('\\', '/');
        string path = Path.Combine(ctx.OutputDirectory, new TileId(0, 0, 0).RelativePath);

        using (var conn = new DuckDBConnection("Data Source=:memory:"))
        {
            conn.Open();
            ArrowTiles.Write(conn, $"SELECT * FROM read_parquet('{parquet}')", path);
        }

        long count = ArrowTiles.RowCount(path);
        return new ReductionResult(new List<TileMeta> { new(0, 0, 0, count, IsLeaf: true) }, count);
    }
}
