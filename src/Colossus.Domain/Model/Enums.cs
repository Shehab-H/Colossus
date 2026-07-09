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


public enum ReductionKind
{
    RawPassthrough,
    QuadtreeLod,
    SignalM4,
    Aggregate,
}

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
