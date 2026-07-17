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
    ISourceAdapterCatalog sources, IReductionCatalog reductions, IBakeStore store, BakePlanner planner,
    IChannelDomainScanner domains, IFactGrouper grouper, FoldRoutingOptions foldRouting)
{
    // The retained facts Parquet the R4 server fold reads, relative to the version directory (RULES R5:
    // baked artifact, never the source DB).
    private const string FactsParquetName = "facts.parquet";

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
        string outputDir = store.VersionDirectory(view.Id, version);

        // The group regime groups the facts to marks first; the reducer then tiles the marks and writes
        // fact companions. Domains, canonical orders, and the reduction view all come from the group
        // artifacts. The row regime is exactly as before: reduce the staging directly.
        GroupArtifacts? group = null;
        var rc = new ReductionContext
        {
            StagingParquetPath = staging,
            OutputDirectory = outputDir,
            Root = plan.Root,
            TilePointBudget = plan.TilePointBudget,
            MaxZoom = plan.MaxZoom,
            View = view,
        };
        IReadOnlyDictionary<string, ChannelDomain> channelDomains;

        if (view.HasMeasures)
        {
            string marks = MarksPath(staging);
            var grouping = grouper.GroupToMarks(staging, marks, view);
            group = GroupRegimeArtifacts.Build(view, grouping, staging, marks, domains);
            channelDomains = group.ChannelDomains;
            rc = rc with
            {
                StagingParquetPath = marks,
                View = group.RenderView,
                GroupRegime = true,
                Companion = group.Companion,
                CanonicalDictionaryOrders = group.RenderCanonicalOrders,
            };
        }
        else
        {
            // Scanned before reduction so the tile writer can order each dictionary column by its
            // canonical domain (tile codes == the client's canonical codes); also feeds the manifest.
            channelDomains = domains.Scan(staging, view);
            rc = rc with { CanonicalDictionaryOrders = CanonicalOrders(view, channelDomains) };
        }

        var result = reductions.Resolve(plan.Reduction).Reduce(rc);

        // R4: a group-regime view retains its facts Parquet per version (the server fold's input) and the
        // planner prices the fold route from the companion bytes the reduction just measured. Additive —
        // the row regime and the static tile serve are untouched (RULES R7).
        string? factsParquet = null;
        FoldRoute? foldRoute = null;
        if (group is not null && result.CompanionPack is { } companionPack)
        {
            File.Copy(group.Companion.FactsParquetPath, Path.Combine(outputDir, FactsParquetName), overwrite: true);
            factsParquet = FactsParquetName;
            foldRoute = FoldRoutePlanner.Price(view, companionPack, result.CompanionSlab, foldRouting);
        }

        int bakedMaxZoom = result.Tiles.Count > 0 ? result.Tiles.Max(t => t.Z) : 0;
        await store.WriteManifestAsync(new Manifest
        {
            Version = version,
            TileFormat = 2,
            View = view,
            Reduction = plan.Reduction,
            Regime = "large",
            Root = plan.Root,
            MinZoom = 0,
            MaxZoom = bakedMaxZoom,
            TilePointBudget = plan.TilePointBudget,
            TotalPoints = result.LeafPointCount,
            SourceRows = probe.Count,
            Tiles = result.Tiles,
            // Full-extract domains (staging sees every row, unlike the sampled root tile the client
            // would otherwise scan). Baked into the manifest so view load costs zero tile fetches.
            ChannelDomains = channelDomains,
            FactChannels = group?.FactChannels,
            CompanionTiles = group is not null,
            GrainChannels = group?.GrainChannels,
            CompanionPack = result.CompanionPack,
            CompanionSlab = result.CompanionSlab,
            FactsParquet = factsParquet,
            FoldRoute = foldRoute,
        }, ct);
        store.PublishLatest(view.Id, version);

        if (foldRoute is not null)
            Console.WriteLine($"  {view.Id}: fold route = {foldRoute.Execution} " +
                $"(worst tile {foldRoute.WorstTileBytes:N0} B vs budget {foldRoute.BudgetBytes:N0} B" +
                (foldRoute.Forced ? ", forced" : "") + ")");

        return new BakeOutcome(view.Id, version, probe.Count, result.Tiles.Count,
            result.Tiles.Count(t => t.IsLeaf), result.LeafPointCount, probe.Bounds, plan.MaxZoom);
    }

    // The grouped marks parquet sits beside the facts staging (<id>.marks.parquet).
    private static string MarksPath(string staging) => Path.ChangeExtension(staging, ".marks.parquet");

    // Canonical dictionary order per dict-encoded channel: its scanned domain values, but only when the
    // scan was complete (a truncated domain has no trustworthy order, so that channel is left to the
    // client's remap). Identity channels aren't dictionary-encoded, so they never appear here.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? CanonicalOrders(
        ViewConfig view, IReadOnlyDictionary<string, ChannelDomain> domains)
    {
        var orders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var name in view.DictionaryEncodedChannels())
            if (domains.TryGetValue(name, out var d) && d.Values is { Count: > 0 } vals && d.ValuesTruncated != true)
                orders[name] = vals;
        return orders.Count > 0 ? orders : null;
    }

    private static string NewVersion() =>
        "v" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}
