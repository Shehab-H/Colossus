using Colossus.Domain.Measures;
using Colossus.Domain.Model;

namespace Colossus.Domain.Reduction;

/// <summary>What a group-regime reduction needs to also emit fact companions (GROUP-MEASURES §4): the
/// raw facts, the grain channels they group by (perFact dict + temporal), and the partial columns the
/// declared measures fold from. Null in the row regime.</summary>
public sealed record CompanionSpec
{
    public required string FactsParquetPath { get; init; }
    public required IReadOnlyList<ChannelSpec> GrainChannels { get; init; }
    public required IReadOnlyList<Partial> Partials { get; init; }
    /// <summary>Canonical value order for grain dict channels, so companion codes equal the codes the
    /// matching argmax measure column carries on the render tiles (client folds argmax into these).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CanonicalDictionaryOrders { get; init; }
}

public sealed record ReductionContext
{
    public required string StagingParquetPath { get; init; }
    public required string OutputDirectory { get; init; }
    public required Bbox Root { get; init; }
    public required int TilePointBudget { get; init; }
    public required int MaxZoom { get; init; }
    public required ViewConfig View { get; init; }

    /// <summary>Group regime (GROUP-MEASURES): <see cref="View"/> is the effective marks view and the
    /// staging is the grouped marks table, so tiles also carry the mark <c>id</c> and each dict channel
    /// (merged sub-pixel cells take the grid key and the mode). Default false — the row regime is
    /// byte-for-byte unchanged.</summary>
    public bool GroupRegime { get; init; }

    /// <summary>When set (group regime), the reducer writes a <c>z/x/y.facts.arrow</c> companion beside
    /// each render tile — fact partials at grain, keyed to the tile's marks by <c>mk</c>.</summary>
    public CompanionSpec? Companion { get; init; }

    /// <summary>Canonical value order per dictionary-encoded channel (its full-extract domain, when not
    /// truncated), so tiles write dictionaries in this order and tile-local codes equal the canonical
    /// codes the client filters/colors by — no client remap. Null/absent channel → per-tile first-seen
    /// order (truncated domain), and the client falls back to remapping that channel.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? CanonicalDictionaryOrders { get; init; }
}

public sealed record ReductionResult(IReadOnlyList<TileMeta> Tiles, long LeafPointCount);

/// <summary>Turns a sorted staging table into tiles. One implementation per <see cref="ReductionKind"/>;
/// a strategy chooses which real rows land in which tile — never the schema, never the mark.</summary>
public interface IReductionStrategy
{
    ReductionKind Kind { get; }
    ReductionResult Reduce(ReductionContext context);
}
