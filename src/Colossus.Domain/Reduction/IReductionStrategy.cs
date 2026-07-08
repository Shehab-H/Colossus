using Colossus.Domain.Model;

namespace Colossus.Domain.Reduction;

public sealed record ReductionContext
{
    public required string StagingParquetPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required Bbox Root { get; init; }
    public required int TilePointBudget { get; init; }
    public required int MaxZoom { get; init; }
    public required ViewConfig View { get; init; }
}

public sealed record ReductionResult(IReadOnlyList<TileMeta> Tiles, long LeafPointCount);

/// <summary>Turns a sorted staging table into tiles. One implementation per <see cref="ReductionKind"/>;
/// a strategy chooses which real rows land in which tile — never the schema, never the mark.</summary>
public interface IReductionStrategy
{
    ReductionKind Kind { get; }
    ReductionResult Reduce(ReductionContext context);
}
