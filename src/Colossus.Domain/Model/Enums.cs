namespace Colossus.Domain.Model;

// These serialize as camelCase strings (see the JSON options in Infrastructure); author configs
// accordingly, e.g. "geo", "quadtreeLod", "lonLat", "multiSelect".

public enum Viewport
{
    Geo,
    Orthographic,
}

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

/// <summary>The closed set of reduction primitives — the axis that makes 100M feasible, chosen from
/// the data and encoding, never the chart's name.</summary>
public enum ReductionKind
{
    RawPassthrough,
    QuadtreeLod,
    SignalM4,
    Aggregate,
}

/// <summary>How a source expresses the spatial role; an adapter normalizes each to a representative
/// (x, y) plus vertices for shapes.</summary>
public enum GeometryKind
{
    Xy,
    LonLat,
    Quadkey,
    Wkt,
    Geohash,
    H3,
}

public enum ChannelRole
{
    Measure,
    Dimension,
    Temporal,
    Identity,
}

public enum ChannelType
{
    F32,
    F64,
    U8,
    U16,
    I32,
    I64,
    Dict,
    Date,
}

public enum ControlKind
{
    Select,
    MultiSelect,
    DateRange,
    Range,
}

public enum AggregateWhen
{
    Client,
    Bake,
}
