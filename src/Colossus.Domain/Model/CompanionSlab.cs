namespace Colossus.Domain.Model;

/// <summary>Group-regime slab companion metadata (docs/companion-scale/SLAB-FORMAT.md), the client's gate
/// onto the slab decode/fold path. Null on row-form bakes (the client keeps the row path). Axes are listed
/// in canonical cell order — categorical outer, the ordered axis fastest (last) — so the client derives the
/// identical strides. <see cref="Cells"/> is the axis cross-product size.</summary>
public sealed record CompanionSlab
{
    /// <summary>The view's default layout — "sparse" (CSR) or "dense" (cell-major, cumulative) — chosen from
    /// the view's global occupancy. Every tile not named in <see cref="TileLayouts"/> uses this; an older
    /// client that ignores the per-tile field decodes the whole view as this.</summary>
    public required string Layout { get; init; }
    public required int Cells { get; init; }
    public required double Occupancy { get; init; }
    public required IReadOnlyList<SlabAxis> Axes { get; init; }
    public required IReadOnlyList<SlabPartial> Partials { get; init; }

    /// <summary>Per-leaf-tile layout overrides (SLAB-FORMAT §3): <c>tileKey → "dense"|"sparse"</c> for the
    /// tiles whose own measured occupancy disagrees with <see cref="Layout"/>. Only the exceptions are
    /// listed (skewed density makes them the minority), so the map is small. Null ⇒ every tile uses
    /// <see cref="Layout"/> (a uniform view, or a bake that predates per-tile choice). The C# reader can
    /// also read a tile's layout physically — a sparse tile has an <c>@idx</c> block, a dense one does not —
    /// but the client, which must know a tile's layout before it fetches, reads this field.</summary>
    public IReadOnlyDictionary<string, string>? TileLayouts { get; init; }

    /// <summary>The layout of one tile: its override if any, else the view default. The single point both
    /// the client and any C# caller resolve a per-tile choice through.</summary>
    public string LayoutOf(string tileKey) =>
        TileLayouts is not null && TileLayouts.TryGetValue(tileKey, out var l) ? l : Layout;
}

/// <summary>One companion grain channel as a slab axis. <see cref="Kind"/> is "categorical" (equality) or
/// "ordered" (range); <see cref="Domain"/> is the full ordered list of values (categorical: canonical dict
/// order; ordered: the sorted bin list) — self-contained so the client needn't re-derive it (the manifest's
/// temporal channelDomain only records min/max). <see cref="Cumulative"/> marks the one ordered axis whose
/// dense subtractable planes are prefix-summed.</summary>
public sealed record SlabAxis(string Name, string Kind, int Cardinality, bool Cumulative, IReadOnlyList<string> Domain);

/// <summary>A slab partial plane's name (the row-form partial name) and element type ("f32" or "i32").</summary>
public sealed record SlabPartial(string Name, string Type);
