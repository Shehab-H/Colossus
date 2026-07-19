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
    IChannelDomainScanner domains, IFactGrouper grouper, FoldRoutingOptions foldRouting,
    ITileCompressor compressor, ITilePacker packer)
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
        string outputDir = store.VersionDirectory(view.Id, version);

        // Area marks carry geometry, so they bake as tile format 3 (geometry replaced by the encoded geom3
        // payload, RULES R3 / tile-transfer Phase 2). Point marks have no geometry and stay format 2.
        int tileFormat = view.Mark is Mark.Polygon or Mark.Rect or Mark.Heat ? 3 : 2;

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
            TileFormat = tileFormat,
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

        // Tile-transfer initiative (Phase 3): pack every tile's columns into one per-version archive, so a
        // first paint ships geometry + the active colour channel and never the measure planes it will not
        // read. This supersedes Phase 1's per-tile .br siblings — the pack compresses per block internally
        // (Content-Encoding does not compose with Range), so brotli would only compress files the pack is
        // about to delete. Phase 1 stays the path for already-published versions, which the manifest gates.
        var renderPack = packer.PackVersion(outputDir, result.Tiles,
            RenderPack.FirstPaintChannels(view, plan.Reduction, group?.FactChannels.PerFact));
        if (renderPack is null)
        {
            var compression = compressor.CompressVersionTiles(outputDir);
            if (compression.Files > 0)
                Console.WriteLine($"  {view.Id}: brotli {compression.Files} tiles, " +
                    $"{compression.OriginalBytes / 1_048_576.0:N1} → {compression.CompressedBytes / 1_048_576.0:N1} MB " +
                    $"({compression.Ratio:0.00}x)");
        }
        else
        {
            long packBytes = new FileInfo(Path.Combine(outputDir, renderPack.File)).Length;
            Console.WriteLine($"  {view.Id}: render pack {renderPack.Entries.Count} tiles, " +
                $"{packBytes / 1_048_576.0:N1} MB at rest, first paint [{string.Join(", ", renderPack.FirstPaint)}]");
        }

        // R4: a group-regime reduction retains the facts the server fold reads (per version, beside the
        // tiles) and the planner prices the fold route from the companion bytes it just measured. Additive
        // — the row regime and the static tile serve are untouched (RULES R7).
        FoldRoute? foldRoute = result.CompanionPack is { } companionPack
            ? FoldRoutePlanner.Price(view, companionPack, result.CompanionSlab, foldRouting)
            : null;

        int bakedMaxZoom = result.Tiles.Count > 0 ? result.Tiles.Max(t => t.Z) : 0;
        await store.WriteManifestAsync(new Manifest
        {
            Version = version,
            TileFormat = tileFormat,
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
            RenderPack = renderPack,
            FactsParquet = result.FactsParquet,
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
