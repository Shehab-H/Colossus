namespace Colossus.Core.Model;

/// <summary>Where the bake reads from and which columns map to the view's primary dims + channels.</summary>
public sealed record SourceSpec
{
    public required string Table { get; init; }
    public required string XColumn { get; init; }
    public required string YColumn { get; init; }
    public string? ValueColumn { get; init; }
    public string? CategoryColumn { get; init; }
}

/// <summary>
/// A declarative visualization: a viewport + mark + reduction primitive + column mapping.
/// The engine knows nothing about "map" or "scatter" — those are just descriptors.
/// </summary>
public sealed record ViewDescriptor
{
    public required string Id { get; init; }
    public required Viewport Viewport { get; init; }
    public required Mark Mark { get; init; }
    public required ReductionKind Reduction { get; init; }
    public required SourceSpec Source { get; init; }
}
