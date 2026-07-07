using Colossus.Core.Model;

namespace Colossus.Core.Reduction;

/// <summary>Inputs a reduction strategy needs to turn a pre-sorted staging table into tiles on disk.</summary>
public sealed record ReductionContext
{
    /// <summary>Path to the staging Parquet extracted from the source (already Hilbert-sorted).</summary>
    public required string StagingParquetPath { get; init; }

    /// <summary>Directory to write tile Arrow files into (tiles land at &lt;OutputDir&gt;/z/x/y.arrow).</summary>
    public required string OutputDir { get; init; }

    /// <summary>Padded, square bounds the pyramid is built inside.</summary>
    public required Bbox Root { get; init; }

    public required int TilePointBudget { get; init; }
    public required int MaxZoom { get; init; }
    public required ViewDescriptor View { get; init; }
}

/// <summary>What a reduction produced — the tile list that goes into the manifest.</summary>
public sealed record ReductionResult(IReadOnlyList<TileMeta> Tiles, long TotalPoints);

/// <summary>
/// The plugin seam. Each member of <see cref="ReductionKind"/> has one implementation.
/// M1 ships QuadtreeLod (and a trivial RawPassthrough); SignalM4 and Aggregate arrive in M2.
/// </summary>
public interface IReductionStrategy
{
    ReductionKind Kind { get; }
    ReductionResult Reduce(ReductionContext context);
}
