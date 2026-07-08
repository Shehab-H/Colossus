using System.Globalization;
using Colossus.Domain.Model;
using Colossus.Domain.Sources;

namespace Colossus.Infrastructure.ClickHouse;

/// <summary>Wraps a view's <c>source.query</c> in a subquery and normalizes whatever geometry it
/// declares into the canonical schema — a representative <c>(x, y)</c> plus, for shapes, interleaved
/// <c>geometry</c> vertices and <c>part_offsets</c> rings — then casts each channel to its canonical
/// type, applies bake filters, and orders by server-side <c>hilbertEncode</c>. Source shape never
/// leaks past here: adding a geometry kind is a new case in <see cref="Geometry"/>, nothing else.</summary>
public sealed class ClickHouseAdapter(ClickHouseClient clickHouse) : ISourceAdapter
{
    public async Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default)
    {
        string sql = $"SELECT min(x), max(x), min(y), max(y), count(), uniqExact((x, y)) FROM (\n{Bounds(view)}\n) FORMAT TabSeparated";
        string[] f = (await clickHouse.QueryTextAsync(sql, ct)).Trim().Split('\t');
        double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
        long L(int i) => long.Parse(f[i], CultureInfo.InvariantCulture);
        return new SourceBounds(new Bbox(D(0), D(2), D(1), D(3)), L(4), L(5));
    }

    // The Hilbert ORDER BY sorts the whole result; on a large source that exceeds RAM, so let the
    // server spill to disk (external sort) rather than hold it all in memory.
    private const long ExternalSortThresholdBytes = 1L << 30;

    public Task ExtractAsync(ViewConfig view, Bbox bounds, string destinationParquet, CancellationToken ct = default) =>
        clickHouse.QueryToFileAsync(
            Extract(view, HilbertOrder(bounds))
            + $"\nSETTINGS max_bytes_before_external_sort = {ExternalSortThresholdBytes}"
            + "\nFORMAT Parquet",
            destinationParquet, ct);

    // Just x, y — the cheap projection the bounds probe needs.
    private static string Bounds(ViewConfig view)
    {
        var g = Geometry(view.Source.Geometry);
        return $"""
            SELECT {g.X} AS x, {g.Y} AS y
            FROM (
            {Decoded(view.Source.Query, g)}
            ) AS src
            """;
    }

    // Full canonical projection: x, y, then geometry/part_offsets for shapes, then channels.
    private static string Extract(ViewConfig view, string order)
    {
        var g = Geometry(view.Source.Geometry);
        string extras = string.Concat(g.Extras.Select(e => $",\n       {e.Expr} AS {e.Alias}"));
        string channels = string.Concat(view.Source.Channels.Select(c => $",\n       {Cast(c)} AS `{c.Name}`"));
        string where = view.BakeFilters is { Count: > 0 } filters
            ? "\nWHERE " + string.Join(" AND ", filters.Select(p => $"({p})"))
            : "";
        return $"""
            SELECT {g.X} AS x, {g.Y} AS y{extras}{channels}
            FROM (
            {Decoded(view.Source.Query, g)}
            ) AS src{where}
            ORDER BY {order}
            """;
    }

    // Injects a kind's derived columns (e.g. a decoded tile index) once, so x/y/geometry reference
    // them instead of recomputing. Point kinds have no prelude and pass the query through unchanged.
    private static string Decoded(string query, GeometrySql g)
    {
        if (g.Prelude.Count == 0) return query;
        string cols = string.Join(",\n       ", g.Prelude.Select(p => $"{p.Expr} AS {p.Alias}"));
        return $"""
            SELECT *,
                   {cols}
            FROM (
            {query}
            ) AS src0
            """;
    }

    private static GeometrySql Geometry(GeometrySpec g) => g.Kind switch
    {
        GeometryKind.Xy => Point(g.X!, g.Y!),
        GeometryKind.LonLat => Point(g.Lon!, g.Lat!),
        GeometryKind.Quadkey => Quadkey(g.Column!),
        _ => throw new NotSupportedException(
            $"ClickHouse adapter: geometry kind '{g.Kind}' is not implemented yet."),
    };

    private static GeometrySql Point(string x, string y) =>
        new([], $"toFloat32({x})", $"toFloat32({y})", []);

    // Bing quadkey → tile index (decoded once in the prelude), then a centroid point plus the cell's
    // four corners as a closed, interleaved lon/lat ring. Zoom-independent: ring math uses the key
    // length, so a level-10 and a level-20 key both normalize the same way.
    private static GeometrySql Quadkey(string col)
    {
        string chars = $"extractAll({col}, '.')";
        string tx = $"arraySum((c, i) -> if(c IN ('1', '3'), bitShiftLeft(toUInt64(1), toUInt64(length({col}) - i)), toUInt64(0)), {chars}, arrayEnumerate({chars}))";
        string ty = $"arraySum((c, i) -> if(c IN ('2', '3'), bitShiftLeft(toUInt64(1), toUInt64(length({col}) - i)), toUInt64(0)), {chars}, arrayEnumerate({chars}))";
        string size = $"bitShiftLeft(toUInt64(1), toUInt64(length({col})))";

        (string, string)[] prelude = [(tx, "_tx"), (ty, "_ty"), (size, "_size")];

        string Lon(string off) => $"(_tx + {off}) / _size * 360 - 180";
        string Lat(string off) => $"degrees(atan(sinh(pi() * (1 - 2 * (_ty + {off}) / _size))))";

        string x = $"toFloat32({Lon("0.5")})";
        string y = $"toFloat32({Lat("0.5")})";
        string ring = $"[{Lon("0")}, {Lat("0")}, {Lon("1")}, {Lat("0")}, {Lon("1")}, {Lat("1")}, {Lon("0")}, {Lat("1")}, {Lon("0")}, {Lat("0")}]";
        (string, string)[] extras =
        [
            ($"arrayMap(v -> toFloat32(v), {ring})", "geometry"),
            ("[toInt32(0), toInt32(5)]", "part_offsets"),
        ];
        return new GeometrySql(prelude, x, y, extras);
    }

    private static string Cast(ChannelSpec c) => c.Type switch
    {
        ChannelType.F32 => $"toFloat32({c.Column})",
        ChannelType.F64 => $"toFloat64({c.Column})",
        ChannelType.U8 => $"toUInt8({c.Column})",
        ChannelType.U16 => $"toUInt16({c.Column})",
        ChannelType.I32 => $"toInt32({c.Column})",
        ChannelType.I64 => $"toInt64({c.Column})",
        ChannelType.Date => $"toDate({c.Column})",
        ChannelType.Dict => $"toString({c.Column})",
        _ => c.Column,
    };

    private static string HilbertOrder(Bbox b)
    {
        if (b.SpanX <= 0 || b.SpanY <= 0) return "rand()";
        return $"hilbertEncode({Grid("x", b.MinX, b.SpanX)}, {Grid("y", b.MinY, b.SpanY)})";

        static string Grid(string col, double min, double span) =>
            $"toUInt32(least(65535, greatest(0, ({col} - {R(min)}) / {R(span)} * 65535)))";
    }

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The SQL fragments a geometry kind contributes: optional derived <paramref name="Prelude"/>
    /// columns, the representative <paramref name="X"/>/<paramref name="Y"/>, and optional shape
    /// <paramref name="Extras"/> (geometry, part_offsets).</summary>
    private sealed record GeometrySql(
        IReadOnlyList<(string Expr, string Alias)> Prelude,
        string X, string Y,
        IReadOnlyList<(string Expr, string Alias)> Extras);
}
