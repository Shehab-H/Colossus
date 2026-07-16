namespace Colossus.Domain.Model;

/// <summary>Group-regime slab companion metadata (docs/companion-scale/SLAB-FORMAT.md), the client's gate
/// onto the slab decode/fold path. Null on row-form bakes (the client keeps the row path). Axes are listed
/// in canonical cell order — categorical outer, the ordered axis fastest (last) — so the client derives the
/// identical strides. <see cref="Cells"/> is the axis cross-product size.</summary>
public sealed record CompanionSlab
{
    /// <summary>"sparse" (CSR) or "dense" (cell-major, cumulative) — chosen from measured occupancy.</summary>
    public required string Layout { get; init; }
    public required int Cells { get; init; }
    public required double Occupancy { get; init; }
    public required IReadOnlyList<SlabAxis> Axes { get; init; }
    public required IReadOnlyList<SlabPartial> Partials { get; init; }
}

/// <summary>One companion grain channel as a slab axis. <see cref="Kind"/> is "categorical" (equality) or
/// "ordered" (range); <see cref="Domain"/> is the full ordered list of values (categorical: canonical dict
/// order; ordered: the sorted bin list) — self-contained so the client needn't re-derive it (the manifest's
/// temporal channelDomain only records min/max). <see cref="Cumulative"/> marks the one ordered axis whose
/// dense subtractable planes are prefix-summed.</summary>
public sealed record SlabAxis(string Name, string Kind, int Cardinality, bool Cumulative, IReadOnlyList<string> Domain);

/// <summary>A slab partial plane's name (the row-form partial name) and element type ("f32" or "i32").</summary>
public sealed record SlabPartial(string Name, string Type);
