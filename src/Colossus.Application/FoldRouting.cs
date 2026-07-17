using Colossus.Domain.Measures;
using Colossus.Domain.Model;

namespace Colossus.Application;

/// <summary>Bake-time configuration for R4 fold routing (bound from the <c>FoldRouting</c> config section;
/// documented default in docs/DEPLOY.md). A plain record so the Application layer stays free of the options
/// package — <see cref="Colossus.Infrastructure.DependencyInjection"/> reads the section and registers one.</summary>
public sealed class FoldRoutingOptions
{
    public const string Section = "FoldRouting";

    /// <summary>Per-interaction worst-tile companion transfer above which a group-regime view routes its
    /// fold to the server — the "single-tile number that hurts" (REQUIREMENTS.md).
    ///
    /// Default 32 MB. The budget has to encode a limit the CLIENT actually hits, not merely a large number:
    /// after R5's plane split the reference views' worst leaf interaction measures ~8 MB, which the browser
    /// folds in tens of ms, so they stay client — while REQUIREMENTS' design scenario (a dense leaf costing
    /// *tens of MB* per interaction) prices remote. Set it below a view's measured worstTileBytes (the bake
    /// logs and the manifest record it) to route that view remote.</summary>
    public long BudgetBytes { get; set; } = 32_000_000;

    /// <summary>Force every group-regime view to the remote route regardless of price — for benchmarking
    /// and testing the server executor against an in-budget view (the R4 force flag).</summary>
    public bool ForceRemote { get; set; }

    /// <summary>Screenful size for the viewport-cost estimate recorded in the manifest (diagnostics only).</summary>
    public int ViewportTiles { get; set; } = 12;
}

/// <summary>Prices a group-regime view's per-interaction fold cost from the MEASURED companion bytes the
/// bake just wrote (the R5 plane-split directory) and picks the fold route. Data-agnostic: it reads the
/// declared color measure's partial planes and the pack's byte ranges — no channel names, no data shape.</summary>
public static class FoldRoutePlanner
{
    public static FoldRoute Price(ViewConfig view, CompanionPack pack, CompanionSlab? slab, FoldRoutingOptions opt)
    {
        // The archetypal single-measure recolor is the color measure; its plane-split fetch is what an
        // interaction moves on the local route (R5). Fall back to the first measure if color isn't one.
        var measures = view.Measures ?? [];
        string? colorName = view.Encoding?.Color?.Channel;
        var color = measures.FirstOrDefault(m => m.Name == colorName) ?? measures.FirstOrDefault();
        var planeNames = color is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : MeasurePartials.For([MeasureParser.Parse(color.Expr)]).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        bool sparse = slab?.Layout == "sparse";

        // Per-tile interaction cost = the compressed bytes the fold fetches for that tile. Slab: the color
        // measure's partial planes (+ the CSR structure block on sparse). Row form: the whole tile block.
        var costs = new List<long>();
        if (pack.PlaneEntries is { } planeDir)
        {
            foreach (var (_, planes) in planeDir)
            {
                long c = 0;
                foreach (var (name, range) in planes)
                    if (planeNames.Contains(name) || (sparse && name == "@idx"))
                        c += range.Length > 1 ? range[1] : 0;
                costs.Add(c);
            }
        }
        else
        {
            foreach (var (_, range) in pack.Entries) costs.Add(range.Length > 1 ? range[1] : 0);
        }

        long worst = costs.Count > 0 ? costs.Max() : 0;
        long viewportEstimate = costs.OrderByDescending(c => c).Take(Math.Max(1, opt.ViewportTiles)).Sum();
        bool remote = opt.ForceRemote || worst > opt.BudgetBytes;

        return new FoldRoute
        {
            Execution = remote ? "remote" : "client",
            WorstTileBytes = worst,
            ViewportEstimateBytes = viewportEstimate,
            BudgetBytes = opt.BudgetBytes,
            Forced = opt.ForceRemote,
        };
    }
}
