using System.Text.Json.Serialization;

namespace Colossus.Core.Model;

/// <summary>Which deck.gl viewport a view renders into. Feasibility-neutral.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Viewport
{
    /// <summary>Geographic: MapLibre basemap, positions are lon/lat degrees.</summary>
    Geo,

    /// <summary>Non-geo Cartesian plane: OrthographicView, positions are arbitrary x/y.</summary>
    Orthographic,
}

/// <summary>How a mark is drawn (deck.gl layer + channel mapping). Feasibility-neutral.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Mark
{
    Point,
    Line,
    Arc,
    Rect,
    Polygon,
    Heat,
    Text,
}

/// <summary>
/// The closed set of data-reduction primitives — the axis that actually makes 100M feasible.
/// Selected from the data + encoding, never from the chart's name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReductionKind
{
    /// <summary>Mark count under budget: ship all, no reduction.</summary>
    RawPassthrough,

    /// <summary>2-D overplot (maps, scatter, bubble): adaptive quadtree pyramid with random-prefix sampling.</summary>
    QuadtreeLod,

    /// <summary>Dense ordered series → per-pixel-column min/max/first/last. Pixel-lossless. (M2)</summary>
    SignalM4,

    /// <summary>Chart is definitionally an aggregate (histogram, bar, heatmap grid). Computed in SQL. (M2)</summary>
    Aggregate,
}
