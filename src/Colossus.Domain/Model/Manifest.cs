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
    public required IReadOnlyList<TileMeta> Tiles { get; init; }
}

/// <summary>The single mutable pointer, flipped atomically once a bake commits.</summary>
public sealed record LatestPointer(string Version);
