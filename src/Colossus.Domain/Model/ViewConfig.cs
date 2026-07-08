namespace Colossus.Domain.Model;

/// <summary>A declarative, uploadable view (docs/VIEW_CONFIG.md) — the one artifact you author to add
/// a visualization. The engine knows nothing of "map" or "scatter"; those are just descriptors.</summary>
public sealed record ViewConfig
{
    public int SchemaVersion { get; init; } = 1;

    public required string Id { get; init; }
    public string? Title { get; init; }

    public required Viewport Viewport { get; init; }
    public required Mark Mark { get; init; }
    // Reduction is chosen by the bake planner from the data's shape (see ReductionPlanner), not
    // authored. Kept as an optional, currently-ignored hint so older configs still deserialize.
    public ReductionKind? Reduction { get; init; }
    public required SourceSpec Source { get; init; }

    public IReadOnlyList<string>? BakeFilters { get; init; }
    public IReadOnlyList<FilterSpec>? Filters { get; init; }
    public AggregateSpec? Aggregate { get; init; }
    public StorageSpec? Storage { get; init; }
    public EncodingSpec? Encoding { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("view config: 'id' is required");
        if (Source is null || string.IsNullOrWhiteSpace(Source.Query))
            throw new ArgumentException($"view '{Id}': 'source.query' is required");
        Source.Geometry.Validate(Id);
    }
}

/// <summary>A source is a query plus a role mapping — never a bare table.</summary>
public sealed record SourceSpec
{
    public string Adapter { get; init; } = "clickhouse";
    public required string Query { get; init; }
    public required GeometrySpec Geometry { get; init; }
    public IReadOnlyList<ChannelSpec> Channels { get; init; } = Array.Empty<ChannelSpec>();
}

/// <summary>The spatial role, a tagged union on <see cref="GeometryKind"/>: only the chosen kind's
/// fields are set, and an adapter resolves it to a representative (x, y) plus vertices for shapes.</summary>
public sealed record GeometrySpec
{
    public required GeometryKind Kind { get; init; }

    public string? X { get; init; }
    public string? Y { get; init; }
    public string? Lon { get; init; }
    public string? Lat { get; init; }

    public string? Column { get; init; }
    public bool Geographic { get; init; } = true;

    public void Validate(string viewId)
    {
        switch (Kind)
        {
            case GeometryKind.Xy when string.IsNullOrWhiteSpace(X) || string.IsNullOrWhiteSpace(Y):
                throw new ArgumentException($"view '{viewId}': geometry.kind=xy needs 'x' and 'y'");
            case GeometryKind.LonLat when string.IsNullOrWhiteSpace(Lon) || string.IsNullOrWhiteSpace(Lat):
                throw new ArgumentException($"view '{viewId}': geometry.kind=lonLat needs 'lon' and 'lat'");
            case GeometryKind.Quadkey or GeometryKind.Wkt or GeometryKind.Geohash or GeometryKind.H3
                when string.IsNullOrWhiteSpace(Column):
                throw new ArgumentException($"view '{viewId}': geometry.kind={Kind} needs 'column'");
        }
    }
}

/// <summary>A carried, typed channel. A dimension is interactively filterable only if carried here.</summary>
public sealed record ChannelSpec
{
    public required string Name { get; init; }
    public required string Column { get; init; }
    public required ChannelRole Role { get; init; }
    public required ChannelType Type { get; init; }
}

public sealed record FilterSpec
{
    public required string Channel { get; init; }
    public required ControlKind Control { get; init; }
    public string? Default { get; init; }
}

public sealed record AggregateSpec
{
    public required IReadOnlyList<string> By { get; init; }
    public IReadOnlyDictionary<string, string> Measures { get; init; } = new Dictionary<string, string>();
    public AggregateWhen When { get; init; } = AggregateWhen.Client;
}

public sealed record StorageSpec
{
    public string Format { get; init; } = "parquet";
    public IReadOnlyList<string>? PartitionBy { get; init; }
    public IReadOnlyList<string>? SortBy { get; init; }
    public IReadOnlyList<string>? Dictionary { get; init; }
    public IReadOnlyList<string>? Bloom { get; init; }
    public int RowGroupRows { get; init; } = 131_072;
    public string Compression { get; init; } = "zstd";
}

public sealed record EncodingSpec
{
    public ChannelRef? Color { get; init; }
    public ChannelRef? Size { get; init; }
}

public sealed record ChannelRef
{
    public required string Channel { get; init; }
    public string? Scheme { get; init; }
}
