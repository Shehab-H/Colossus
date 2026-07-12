using System.Text.Json.Serialization;

namespace Colossus.Domain.Model;

public readonly record struct TileMeta(int Z, int X, int Y, long Count, bool IsLeaf)
{
    [JsonIgnore] // derived — serializing it doubled every tile entry in manifest.json
    public TileId Id => new(Z, X, Y);
}

/// <summary>The boot document a client reads (via latest.json) to render a baked view.</summary>
public sealed record Manifest
{
    public int SchemaVersion { get; init; } = 1;
    /// <summary>Tile binary format. 2 = the zero-copy contract (single record batch, no nulls,
    /// tile-global triangle indices, canonical dictionaries, f32 measures) the client decodes as
    /// typed-array views over the one fetched buffer. Absent/0/1 on older manifests selects the
    /// copy-based format-1 decode, which the client keeps until every view is re-baked.</summary>
    public int TileFormat { get; init; }
    public required string Version { get; init; }
    public required ViewConfig View { get; init; }
    /// <summary>The reduction the planner chose for this bake (derived from data shape, not authored).
    /// The client reads it to label fidelity — e.g. an aggregate pyramid marks its coarse levels.</summary>
    public required ReductionKind Reduction { get; init; }
    public required string Regime { get; init; }
    public required Bbox Root { get; init; }
    public required int MinZoom { get; init; }
    public required int MaxZoom { get; init; }
    public required int TilePointBudget { get; init; }
    public required long TotalPoints { get; init; }
    /// <summary>Rows the source reported at bake time. <see cref="TotalPoints"/> is *defined* as the leaf
    /// sum, so only this — a number the reducer never produced — can witness that the tiling partitioned
    /// the source rather than duplicating or dropping rows. Null on manifests from older bakes.</summary>
    public long? SourceRows { get; init; }
    public required IReadOnlyList<TileMeta> Tiles { get; init; }
    /// <summary>Per-channel data domains scanned from the full staged extract at bake time, keyed by
    /// channel name. Spares the client a root-tile scan at load and — unlike that tile, which is a
    /// sample — sees every row, so no category can be missing. Null on manifests from older bakes;
    /// the client falls back to its tile scan.</summary>
    public IReadOnlyDictionary<string, ChannelDomain>? ChannelDomains { get; init; }

    /// <summary>Group regime only (GROUP-MEASURES): the derived perMark/perFact split so the client can
    /// route a filter as a GPU predicate (perMark) or a fold context (perFact). Null in the row regime.</summary>
    public FactChannels? FactChannels { get; init; }

    /// <summary>True when the bake wrote a <c>z/x/y.facts.arrow</c> companion beside every tile.</summary>
    public bool CompanionTiles { get; init; }

    /// <summary>The companion grain columns (perFact dict + temporal channels), so the client knows what
    /// dimensions its fact partials are keyed by. Null in the row regime.</summary>
    public IReadOnlyList<string>? GrainChannels { get; init; }
}

/// <summary>The derived channel split of a group-regime view.</summary>
public sealed record FactChannels(IReadOnlyList<string> PerMark, IReadOnlyList<string> PerFact);

/// <summary>One channel's observed domain. Numeric channels carry min/max plus a quantile grid (the
/// client derives quantile-scale breaks from it); non-numeric channels carry their distinct values,
/// capped — a capped list sets <see cref="ValuesTruncated"/> and the client treats it as absent.</summary>
public sealed record ChannelDomain
{
    public IReadOnlyList<string>? Values { get; init; }
    public bool? ValuesTruncated { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<double>? Quantiles { get; init; }
}

/// <summary>The single mutable pointer, flipped atomically once a bake commits.</summary>
public sealed record LatestPointer(string Version);
