using System.Globalization;
using Colossus.Domain.Model;
using Colossus.Domain.Sources;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.ClickHouse.Geometry;

namespace Colossus.Infrastructure.ClickHouse;

/// <summary>Wraps a view's <c>source.query</c> in a subquery and normalizes whatever geometry it
/// declares into the canonical schema — a representative <c>(x, y)</c> plus, for shapes, interleaved
/// <c>geometry</c> vertices and <c>part_offsets</c> rings — then casts each channel to its canonical
/// type, applies bake filters, and orders by server-side <c>hilbertEncode</c>. Source shape never
/// leaks past here: the geometry normalization lives in <see cref="GeometrySqlFactory"/>, so adding a
/// geometry kind is a new <see cref="IGeometrySql"/>, nothing else.</summary>
public sealed class ClickHouseAdapter(ClickHouseClient clickHouse) : ISourceAdapter
{
    public async Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default)
    {
        string sql = $"SELECT min({TileSchema.X}), max({TileSchema.X}), min({TileSchema.Y}), max({TileSchema.Y}), " +
                     $"count(), uniqExact(({TileSchema.X}, {TileSchema.Y})) FROM (\n{Bounds(view)}\n) FORMAT TabSeparated";
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

    // Just x, y — the cheap projection the bounds probe needs. Bake filters apply here too, so the
    // planner sees exactly the rows the extract will ship (bounds, count, and shape ratio all agree).
    private static string Bounds(ViewConfig view)
    {
        var g = GeometrySqlFactory.Build(view.Source.Geometry);
        return $"""
            SELECT {g.X} AS {TileSchema.X}, {g.Y} AS {TileSchema.Y}
            FROM (
            {Decoded(view.Source.Query, g)}
            ) AS src{Where(view)}
            """;
    }

    // Full canonical projection: x, y, then geometry/part_offsets for shapes, then channels.
    private static string Extract(ViewConfig view, string order)
    {
        var g = GeometrySqlFactory.Build(view.Source.Geometry);
        string extras = string.Concat(g.Extras.Select(e => $",\n       {e.Expr} AS {e.Alias}"));
        string channels = string.Concat(view.Source.Channels.Select(c => $",\n       {Cast(c)} AS `{c.Name}`"));
        return $"""
            SELECT {g.X} AS {TileSchema.X}, {g.Y} AS {TileSchema.Y}{extras}{channels}
            FROM (
            {Decoded(view.Source.Query, g)}
            ) AS src{Where(view)}
            ORDER BY {order}
            """;
    }

    private static string Where(ViewConfig view) =>
        view.BakeFilters is { Count: > 0 } filters
            ? "\nWHERE " + string.Join(" AND ", filters.Select(p => $"({p})"))
            : "";

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

    private static string Cast(ChannelSpec c) => c.Type switch
    {
        ChannelType.F32 => $"toFloat32({c.Column})",
        ChannelType.F64 => $"toFloat64({c.Column})",
        ChannelType.U8 => $"toUInt8({c.Column})",
        ChannelType.U16 => $"toUInt16({c.Column})",
        ChannelType.I32 => $"toInt32({c.Column})",
        ChannelType.I64 => $"toInt64({c.Column})",
        ChannelType.Date => $"toDate({c.Column})",
        // Format-2 no-null contract: string/dimension/identity channels can't carry a real null, so a
        // missing value becomes the literal 'null' — matching the client's String(null) rendering and
        // the domain scanner's "null" bucket. Numeric/temporal nulls are handled downstream (measures
        // fold to NaN in the reducers; temporal stays nullable and is rebuilt per filter).
        ChannelType.Dict => $"COALESCE(toString({c.Column}), 'null')",
        _ => c.Column,
    };

    private static string HilbertOrder(Bbox b)
    {
        if (b.SpanX <= 0 || b.SpanY <= 0) return "rand()";
        return $"hilbertEncode({Grid(TileSchema.X, b.MinX, b.SpanX)}, {Grid(TileSchema.Y, b.MinY, b.SpanY)})";

        static string Grid(string col, double min, double span) =>
            $"toUInt32(least(65535, greatest(0, ({col} - {Sql.Lit(min)}) / {Sql.Lit(span)} * 65535)))";
    }
}
