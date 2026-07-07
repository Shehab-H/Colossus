namespace Colossus.Core.Model;

/// <summary>One node in the baked pyramid, as advertised to the client so it never probes 404s.</summary>
public readonly record struct TileMeta(int Z, int X, int Y, long Count, bool IsLeaf)
{
    public TileId Id => new(Z, X, Y);
}

/// <summary>
/// The boot document for a baked dataset. The client reads this (via latest.json) and obeys it —
/// same client code regardless of view type or dataset size.
/// </summary>
public sealed record Manifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Version { get; init; }
    public required ViewDescriptor View { get; init; }
    public required string Regime { get; init; }
    public required Bbox Root { get; init; }
    public required int MinZoom { get; init; }
    public required int MaxZoom { get; init; }
    public required int TilePointBudget { get; init; }
    public required long TotalPoints { get; init; }
    public required IReadOnlyList<TileMeta> Tiles { get; init; }
}

/// <summary>The single mutable pointer flipped atomically after a bake commits.</summary>
public sealed record LatestPointer(string Version);
