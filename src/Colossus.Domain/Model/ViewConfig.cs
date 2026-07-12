using System.Text.Json;
using Colossus.Domain.Measures;

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
    // Reduction is chosen by the bake planner from the data's shape (see BakePlanner), not authored.
    // Kept as an optional, currently-ignored hint so older configs still deserialize.
    public ReductionKind? Reduction { get; init; }
    public required SourceSpec Source { get; init; }

    public IReadOnlyList<string>? BakeFilters { get; init; }
    public IReadOnlyList<FilterSpec>? Filters { get; init; }
    /// <summary>Computed per-mark values (VIEW_CONFIG §4). Presence opts the view into the group
    /// regime; absence is the row regime, byte-for-byte as today.</summary>
    public IReadOnlyList<MeasureSpec>? Measures { get; init; }
    public AggregateSpec? Aggregate { get; init; }
    public StorageSpec? Storage { get; init; }
    public EncodingSpec? Encoding { get; init; }
    public InspectSpec? Inspect { get; init; }

    /// <summary>Declaring a <c>measures</c> block opts the view into the group regime (VIEW_CONFIG §1).
    /// perMark/perFact channel classification is still derived from data at bake, not from this flag.</summary>
    public bool HasMeasures => Measures is { Count: > 0 };

    /// <summary>An id becomes a filename, a tiles/ subdirectory, and a URL slug — restrict it to
    /// kebab-case so an uploaded config can never escape those roots.</summary>
    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>Channels whose tile columns are written Arrow-dictionary-encoded. Purely schema-driven:
    /// a declared <see cref="ChannelType.Dict"/> is categorical by contract, EXCEPT identity channels,
    /// which are per-row-unique by role (names, ids) — a dictionary would inflate those, so they stay
    /// plain UTF-8 and the client decodes rows lazily.</summary>
    public IReadOnlySet<string> DictionaryEncodedChannels() => Source.Channels
        .Where(c => c.Type == ChannelType.Dict && c.Role != ChannelRole.Identity)
        .Select(c => c.Name)
        .ToHashSet(StringComparer.Ordinal);

    public void Validate()
    {
        if (!IsValidId(Id))
            throw new ArgumentException($"view config: id '{Id}' must be non-empty kebab-case ([a-z0-9-])");
        if (Source is null || string.IsNullOrWhiteSpace(Source.Query))
            throw new ArgumentException($"view '{Id}': 'source.query' is required");
        Source.Geometry.Validate(Id);

        // Encoding and inspect may only reference a channel the source carries or a declared measure —
        // otherwise the client asks a tile for a column that was never baked and silently renders nothing.
        var channels = Source.Channels.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var measures = ValidateMeasures(channels);
        var colorable = new HashSet<string>(channels, StringComparer.Ordinal);
        colorable.UnionWith(measures);

        void Require(IReadOnlySet<string> set, string? name, string what, string kind)
        {
            if (!string.IsNullOrEmpty(name) && !set.Contains(name))
                throw new ArgumentException($"view '{Id}': {what} '{name}' is not {kind}");
        }

        Require(colorable, Encoding?.Color?.Channel, "encoding.color.channel", "a declared channel or measure");
        Require(channels, Encoding?.Size?.Channel, "encoding.size.channel", "a declared channel");
        if (Encoding?.Color is { Bins: <= 0 })
            throw new ArgumentException($"view '{Id}': encoding.color.bins must be > 0");
        if (Inspect is { } inspect)
        {
            if (inspect.Channels.Count == 0)
                throw new ArgumentException($"view '{Id}': inspect.channels must list at least one channel");
            Require(colorable, inspect.Title, "inspect.title", "a declared channel or measure");
            foreach (var name in inspect.Channels)
                Require(colorable, name, "inspect channel", "a declared channel or measure");
        }
    }

    /// <summary>Parses and semantically checks the <c>measures</c> block (VIEW_CONFIG §11) and returns
    /// the measure names. Empty (and a no-op) in the row regime. perMark/perFact-specific rules that
    /// need data (argmax over a perMark dim, inspecting a raw perFact channel) are enforced at bake,
    /// after classification — here we check what the config alone can prove.</summary>
    private IReadOnlySet<string> ValidateMeasures(IReadOnlySet<string> channels)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!HasMeasures) return names;

        if (Source.Geometry.Kind != GeometryKind.Quadkey)
            throw new ArgumentException(
                $"view '{Id}': measures require keyed geometry (v0 supports 'quadkey'), not '{Source.Geometry.Kind}'");

        var numeric = Source.Channels.Where(c => IsNumeric(c.Type)).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var dict = Source.Channels.Where(c => c.Type == ChannelType.Dict).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var m in Measures!)
        {
            if (string.IsNullOrWhiteSpace(m.Name))
                throw new ArgumentException($"view '{Id}': a measure is missing 'name'");
            if (channels.Contains(m.Name))
                throw new ArgumentException($"view '{Id}': measure '{m.Name}' collides with a channel of the same name");
            if (!names.Add(m.Name))
                throw new ArgumentException($"view '{Id}': measure '{m.Name}' is declared more than once");

            MeasureExpr ast;
            try { ast = MeasureParser.Parse(m.Expr ?? ""); }
            catch (MeasureParseException ex)
            {
                throw new ArgumentException($"view '{Id}': measure '{m.Name}': {ex.Message}");
            }
            ValidateMeasureSemantics(ast, m.Name, numeric, dict);
        }
        return names;
    }

    private void ValidateMeasureSemantics(MeasureExpr expr, string measure,
        IReadOnlySet<string> numeric, IReadOnlySet<string> dict)
    {
        void RequireNumeric(string ch)
        {
            if (!numeric.Contains(ch))
                throw new ArgumentException($"view '{Id}': measure '{measure}': '{ch}' must be a numeric channel");
        }
        void RequireDict(string ch)
        {
            if (!dict.Contains(ch))
                throw new ArgumentException($"view '{Id}': measure '{measure}': '{ch}' must be a dict (dimension) channel");
        }
        void CheckAgg(Agg agg)
        {
            switch (agg)
            {
                case Sum s: RequireNumeric(s.Channel); break;
                case Avg a: RequireNumeric(a.Channel); break;
                case Min mn: RequireNumeric(mn.Channel); break;
                case Max mx: RequireNumeric(mx.Channel); break;
                case Wavg w: RequireNumeric(w.Channel); RequireNumeric(w.Weight); break;
                case Count: break;
            }
            if (agg.Where is { } wc) RequireDict(wc.Channel);
        }
        switch (expr)
        {
            case Agg agg: CheckAgg(agg); break;
            case Share sh: CheckAgg(sh.Inner); RequireDict(sh.WhereChannel); break;
            case ArgExt ax: RequireDict(ax.Dimension); CheckAgg(ax.Inner); break;
        }
    }

    private static bool IsNumeric(ChannelType t) => t is ChannelType.F32 or ChannelType.F64
        or ChannelType.U8 or ChannelType.U16 or ChannelType.I32 or ChannelType.I64;
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

/// <summary>A computed per-mark value: a virtual channel whose value is a function of the active
/// filters. <see cref="Expr"/> is one expression from the closed grammar (VIEW_CONFIG §4).</summary>
public sealed record MeasureSpec
{
    public required string Name { get; init; }
    public required string Expr { get; init; }
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
    public ColorSpec? Color { get; init; }
    public ChannelRef? Size { get; init; }
}

public sealed record ChannelRef
{
    public required string Channel { get; init; }
    public string? Scheme { get; init; }
}

/// <summary>How a data channel maps to color — a superset scale spec (à la Vega-Lite). The engine only
/// carries it into the manifest; the client builds the actual scale (see web/src/lib/colorScale.ts).
/// Everything but <see cref="Channel"/> is optional; the client infers a scale from the channel's
/// datatype when omitted.</summary>
public sealed record ColorSpec
{
    public required string Channel { get; init; }

    /// <summary>linear | log | sqrt | diverging | quantize | quantile | threshold | ordinal | categorical.</summary>
    public string? Type { get; init; }
    public string? Scheme { get; init; }

    /// <summary>Explicit hex ramp/palette — overrides <see cref="Scheme"/>.</summary>
    public IReadOnlyList<string>? Range { get; init; }

    /// <summary>Numeric [min,max] or an explicit category order (mixed types allowed).</summary>
    public IReadOnlyList<JsonElement>? Domain { get; init; }

    public bool? Reverse { get; init; }
    public double? Midpoint { get; init; }
    public int? Bins { get; init; }
    public IReadOnlyList<double>? Thresholds { get; init; }

    /// <summary>Categorical: explicit value → hex.</summary>
    public IReadOnlyDictionary<string, string>? Palette { get; init; }

    /// <summary>Color for unmapped / null values.</summary>
    public string? Unknown { get; init; }
}

/// <summary>Optional click-to-inspect: when set, clicking a mark pins a panel of these channels' values
/// for that cell. Null (the default) means marks aren't pickable and clicks do nothing.</summary>
public sealed record InspectSpec
{
    /// <summary>Channels shown, top to bottom.</summary>
    public required IReadOnlyList<string> Channels { get; init; }

    /// <summary>Optional channel whose value heads the panel (e.g. an id or the primary measure).</summary>
    public string? Title { get; init; }
}
