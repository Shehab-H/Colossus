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
/// manifest and publish the version. Reaches the outside world only through domain ports; the bake
/// plan (reduction, depth, budget, root) comes from <see cref="BakePlanner"/>.</summary>
public sealed class BakeViewUseCase(
    ISourceAdapterCatalog sources, IReductionCatalog reductions, IBakeStore store, BakePlanner planner)
{
    public async Task<BakeOutcome> BakeAsync(ViewConfig view, CancellationToken ct = default)
    {
        var source = sources.Resolve(view.Source.Adapter);

        var probe = await source.ProbeAsync(view, ct);
        if (probe.Count == 0)
            return BakeOutcome.Empty(view.Id);

        var plan = planner.Plan(probe, view);

        string staging = store.StagingPath(view.Id);
        await source.ExtractAsync(view, probe.Bounds, staging, ct);

        string version = NewVersion();
        var result = reductions.Resolve(plan.Reduction).Reduce(new ReductionContext
        {
            StagingParquetPath = staging,
            OutputDirectory = store.VersionDirectory(view.Id, version),
            Root = plan.Root,
            TilePointBudget = plan.TilePointBudget,
            MaxZoom = plan.MaxZoom,
            View = view,
        });

        int bakedMaxZoom = result.Tiles.Count > 0 ? result.Tiles.Max(t => t.Z) : 0;
        await store.WriteManifestAsync(new Manifest
        {
            Version = version,
            View = view,
            Reduction = plan.Reduction,
            Regime = "large",
            Root = plan.Root,
            MinZoom = 0,
            MaxZoom = bakedMaxZoom,
            TilePointBudget = plan.TilePointBudget,
            TotalPoints = result.LeafPointCount,
            Tiles = result.Tiles,
        }, ct);
        store.PublishLatest(view.Id, version);

        return new BakeOutcome(view.Id, version, probe.Count, result.Tiles.Count,
            result.Tiles.Count(t => t.IsLeaf), result.LeafPointCount, probe.Bounds, plan.MaxZoom);
    }

    private static string NewVersion() =>
        "v" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}
