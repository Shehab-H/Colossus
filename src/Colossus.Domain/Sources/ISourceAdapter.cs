using Colossus.Domain.Model;

namespace Colossus.Domain.Sources;

/// <param name="DistinctGeometries">Distinct representative (x, y) — the count of real *shapes*, as
/// opposed to <paramref name="Count"/> source rows. Their ratio tells the planner whether a source is
/// a point cloud (≈1 row/shape) or a fact cube over few shapes (many rows/shape).</param>
public sealed record SourceBounds(Bbox Bounds, long Count, long DistinctGeometries);

/// <summary>The source seam: the only place that knows a source dialect. It probes bounds + count and
/// extracts the canonical, spatially sorted staging table. Everything after the extract is source-agnostic.</summary>
public interface ISourceAdapter
{
    Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default);
    Task ExtractAsync(ViewConfig view, Bbox bounds, string destinationParquet, CancellationToken ct = default);
}
