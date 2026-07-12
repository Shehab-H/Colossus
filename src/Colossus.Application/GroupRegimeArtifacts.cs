using Colossus.Domain.Baking;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;

namespace Colossus.Application;

/// <summary>Everything a group-regime bake needs beyond the authored view: the effective render view,
/// the assembled channel domains (measures + perMark from the marks staging; perFact filter options and
/// argmax color domains from the facts), the companion spec, and the canonical dict orders that keep an
/// argmax measure's tile codes and its dimension's companion codes identical (GROUP-MEASURES §3–5).</summary>
public sealed record GroupArtifacts(
    ViewConfig RenderView,
    IReadOnlyDictionary<string, ChannelDomain> ChannelDomains,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RenderCanonicalOrders,
    CompanionSpec Companion,
    FactChannels FactChannels,
    IReadOnlyList<string> GrainChannels);

public static class GroupRegimeArtifacts
{
    private static readonly StringComparer Ord = StringComparer.Ordinal;

    public static GroupArtifacts Build(ViewConfig authored, FactGrouping grouping,
        string factsPath, string marksPath, IChannelDomainScanner scanner)
    {
        var renderView = EffectiveView.For(authored, grouping);
        var marksDomains = scanner.Scan(marksPath, renderView);
        var factsDomains = scanner.Scan(factsPath, authored);

        // An argmax measure colours over its dimension's full domain, not just the values that happen to
        // dominate at the default context — a filter can make any dimension value dominant.
        var argmaxDim = new Dictionary<string, string>(Ord);
        foreach (var m in authored.Measures!)
            if (MeasureParser.Parse(m.Expr) is ArgExt ax) argmaxDim[m.Name] = ax.Dimension;

        var perFact = grouping.PerFactChannels.ToHashSet(Ord);
        var domains = new Dictionary<string, ChannelDomain>(Ord);
        foreach (var (name, d) in marksDomains)                       // numeric measures + perMark dims
            if (name != TileSchema.Id && !argmaxDim.ContainsKey(name)) domains[name] = d;
        foreach (var (measure, dim) in argmaxDim)                     // argmax colour domain = its dim's
            if (factsDomains.TryGetValue(dim, out var dd)) domains[measure] = dd;
        foreach (var c in authored.Source.Channels)                  // perFact filter options
            if (perFact.Contains(c.Name) && IsGrain(c) && factsDomains.TryGetValue(c.Name, out var fd))
                domains[c.Name] = fd;

        // Canonical dict order = the baked domain values when the scan is complete. An argmax measure and
        // its dimension resolve to the same domain here, so the render tile's measure codes and the
        // companion's dimension codes index the same order — the client folds argmax straight into it.
        IReadOnlyList<string>? Order(string name) =>
            domains.TryGetValue(name, out var d) && d.Values is { Count: > 0 } v && d.ValuesTruncated != true ? v : null;

        var renderOrders = new Dictionary<string, IReadOnlyList<string>>(Ord);
        foreach (var name in renderView.DictionaryEncodedChannels())
            if (Order(name) is { } o) renderOrders[name] = o;

        var grainChannels = authored.Source.Channels.Where(c => perFact.Contains(c.Name) && IsGrain(c)).ToList();
        var companionOrders = new Dictionary<string, IReadOnlyList<string>>(Ord);
        foreach (var c in grainChannels)
            if (c.Type == ChannelType.Dict && Order(c.Name) is { } o) companionOrders[c.Name] = o;

        var companion = new CompanionSpec
        {
            FactsParquetPath = factsPath,
            GrainChannels = grainChannels,
            Partials = MeasurePartials.For(authored.Measures!.Select(m => MeasureParser.Parse(m.Expr))),
            CanonicalDictionaryOrders = companionOrders.Count > 0 ? companionOrders : null,
        };

        return new GroupArtifacts(renderView, domains,
            renderOrders.Count > 0 ? renderOrders : null, companion,
            new FactChannels(grouping.PerMarkChannels, grouping.PerFactChannels),
            grainChannels.Select(c => c.Name).ToList());
    }

    private static bool IsGrain(ChannelSpec c) =>
        c.Type == ChannelType.Dict || c.Type == ChannelType.Date || c.Role == ChannelRole.Temporal;
}
