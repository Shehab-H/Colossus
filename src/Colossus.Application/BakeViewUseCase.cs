using System.Globalization;
using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Sources;

namespace Colossus.Application;

public sealed record BakeOutcome(
    string ViewId, string? Version, long SourceRows, int TileCount, int LeafCount,
    long LeafPoints, Bbox Bounds, int MaxZoom)
{
    public bool IsEmpty => Version is null;

    public static BakeOutcome Empty(string viewId) => new(viewId, null, 0, 0, 0, 0, default, 0);
}

/// <summary>Bakes one view end to end: probe → plan → extract staging → reduce into tiles → write
/// manifest and publish the version. Reaches the outside world only through domain ports.</summary>
public sealed class BakeViewUseCase(ISourceAdapterCatalog sources, IReductionCatalog reductions, IBakeStore store)
{
    private const int TilePointBudget = 250_000;

    public async Task<BakeOutcome> BakeAsync(ViewConfig view, CancellationToken ct = default)
    {
        var source = sources.Resolve(view.Source.Adapter);

        var probe = await source.ProbeAsync(view, ct);
        if (probe.Count == 0)
            return BakeOutcome.Empty(view.Id);

        Bbox root = probe.Bounds.ToPaddedSquare();
        // Depth is driven by distinct shapes, not rows: a fact cube has many rows per shape, but it is
        // the shapes that must land in leaves under budget. maxZoom is only a cap — the adaptive
        // recursion stops earlier wherever a node is already under budget.
        int maxZoom = PlanMaxZoom(probe.DistinctGeometries, TilePointBudget);
        ReductionKind reductionKind = ReductionPlanner.Select(probe, view, TilePointBudget);

        string staging = store.StagingPath(view.Id);
        await source.ExtractAsync(view, probe.Bounds, staging, ct);

        string version = NewVersion();
        var result = reductions.Resolve(reductionKind).Reduce(new ReductionContext
        {
            StagingParquetPath = staging,
            OutputDirectory = store.VersionDirectory(view.Id, version),
            Root = root,
            TilePointBudget = TilePointBudget,
            MaxZoom = maxZoom,
            View = view,
        });

        int bakedMaxZoom = result.Tiles.Count > 0 ? result.Tiles.Max(t => t.Z) : 0;
        await store.WriteManifestAsync(new Manifest
        {
            Version = version,
            View = view,
            Reduction = reductionKind,
            Regime = "large",
            Root = root,
            MinZoom = 0,
            MaxZoom = bakedMaxZoom,
            TilePointBudget = TilePointBudget,
            TotalPoints = result.LeafPointCount,
            Tiles = result.Tiles,
        }, ct);
        store.PublishLatest(view.Id, version);

        return new BakeOutcome(view.Id, version, probe.Count, result.Tiles.Count,
            result.Tiles.Count(t => t.IsLeaf), result.LeafPointCount, probe.Bounds, maxZoom);
    }

    // Enough depth for even a clustered region's leaves to reach the budget; the adaptive recursion
    // stops earlier wherever a node is already under budget, so a generous cap is free (sparse regions
    // never use the extra depth). The margin absorbs non-uniform density (dense cities vs. empty ocean).
    private static int PlanMaxZoom(long distinctShapes, int budget)
    {
        double perAxis = Math.Sqrt((double)distinctShapes / budget);
        int z = (int)Math.Ceiling(Math.Log2(Math.Max(perAxis, 1))) + 5;
        return Math.Clamp(z, 1, 16);
    }

    private static string NewVersion() =>
        "v" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}
