using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Ships every row as a single root tile — used when the source is already under budget
/// (typically because the view's query aggregated it). Σ leaf rows == source.</summary>
public sealed class RawPassthroughReducer : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.RawPassthrough;

    public ReductionResult Reduce(ReductionContext ctx)
    {
        string path = Path.Combine(ctx.OutputDirectory, new TileId(0, 0, 0).RelativePath);

        using (var db = DuckDbSession.InMemory())
            ArrowTileWriter.Write(db.Connection, $"SELECT * FROM read_parquet('{Sql.Path(ctx.StagingParquetPath)}')", path,
                ctx.View.DictionaryEncodedChannels());

        long count = ArrowTileWriter.RowCount(path);
        return new ReductionResult(new List<TileMeta> { new(0, 0, 0, count, IsLeaf: true) }, count);
    }
}
