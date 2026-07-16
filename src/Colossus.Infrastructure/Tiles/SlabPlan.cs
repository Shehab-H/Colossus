using System.Globalization;
using System.Text;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;

namespace Colossus.Infrastructure.Tiles;

/// <summary>One companion grain channel resolved to a slab axis with its cell-order stride
/// (docs/companion-scale/SLAB-FORMAT.md §2). <see cref="Stride"/> is the product of the cardinalities of
/// all axes inner to it; the innermost (ordered) axis has stride 1.</summary>
internal sealed record SlabAxisPlan(
    string Name, bool Categorical, IReadOnlyList<string> Domain, int Stride, bool Cumulative)
{
    public int Cardinality => Domain.Count;
}

/// <summary>The per-view slab decision (layout + axes + cell space), plus the SQL that maps a companion
/// grain row to its canonical <c>cellId</c>. Chosen once at bake from measured occupancy; the reducer
/// hands it to <see cref="SlabCompanionWriter"/> and projects it into <see cref="Manifest.CompanionSlab"/>.</summary>
internal sealed class SlabPlan
{
    // Occupancy at or above this ⇒ dense (cell-major, cumulative); below ⇒ sparse (CSR). The reference
    // views measure ~0.38, so they are sparse; dense is the high-occupancy path.
    public const double DenseThreshold = 0.5;

    public required bool Dense { get; init; }
    public required int Cells { get; init; }
    public required double Occupancy { get; init; }
    /// <summary>Axes in canonical cell order (categorical outer, ordered fastest/last).</summary>
    public required IReadOnlyList<SlabAxisPlan> Axes { get; init; }
    public required IReadOnlyList<Partial> Partials { get; init; }
    /// <summary>Dense cumulation walks this axis (stride, cardinality) — the one cumulative ordered axis.</summary>
    public required int CumulativeStride { get; init; }
    public required int CumulativeCardinality { get; init; }

    public string Layout => Dense ? "dense" : "sparse";

    public CompanionSlab ToManifest() => new()
    {
        Layout = Layout,
        Cells = Cells,
        Occupancy = Occupancy,
        Axes = [.. Axes.Select(a => new SlabAxis(a.Name,
            a.Categorical ? "categorical" : "ordered", a.Cardinality, a.Cumulative, a.Domain))],
        Partials = [.. Partials.Select(p => new SlabPartial(p.Name, p.Kind == PartialKind.Count ? "i32" : "f32"))],
    };

    /// <summary>SQL scalar mapping the axis grain columns to a canonical cellId (Σ codeᵢ·strideᵢ), for a
    /// companion query. A value is resolved to its 0-based code by <c>list_position</c> over the axis's
    /// literal domain — categorical on the raw string, ordered on the date cast to its <c>YYYY-MM-DD</c>
    /// form (the domain values). Domains are the full extract, so every grain value is present.</summary>
    public string CellIdSql(string tableAlias = "")
    {
        if (Axes.Count == 0) return "0";
        string col(string name) => tableAlias.Length > 0 ? $"{tableAlias}.\"{name}\"" : $"\"{name}\"";
        var terms = Axes.Select(a =>
        {
            string val = a.Categorical ? col(a.Name) : $"CAST({col(a.Name)} AS VARCHAR)";
            string code = $"(list_position({DomainLiteral(a.Domain)}, {val}) - 1)";
            return a.Stride == 1 ? code : $"{code} * {a.Stride}";
        });
        return string.Join(" + ", terms);
    }

    private static string DomainLiteral(IReadOnlyList<string> domain) =>
        "[" + string.Join(", ", domain.Select(v => $"'{v.Replace("'", "''")}'")) + "]";
}

internal static class SlabPlanner
{
    /// <summary>Resolves the companion grain into axes, measures leaf occupancy, and picks the layout.
    /// <paramref name="factsTable"/>/<paramref name="marksTable"/> are the DuckDB tables the reducer has
    /// already loaded (raw facts, grouped marks). Categorical domains come from the companion's canonical
    /// dict orders; ordered domains are scanned distinct here (the manifest's temporal domain is only
    /// min/max).</summary>
    public static SlabPlan Plan(DuckDbSession db, CompanionSpec companion, string factsTable, string marksTable)
    {
        var categorical = new List<SlabAxisPlan>();
        var ordered = new List<SlabAxisPlan>();
        foreach (var ch in companion.GrainChannels)
        {
            bool cat = ch.Type == ChannelType.Dict;
            IReadOnlyList<string> domain = cat
                ? companion.CanonicalDictionaryOrders?.GetValueOrDefault(ch.Name) ?? ScanDomain(db, factsTable, ch.Name)
                : ScanDomain(db, factsTable, ch.Name);
            (cat ? categorical : ordered).Add(new SlabAxisPlan(ch.Name, cat, domain, 1, false));
        }

        // Cell order: categorical axes (grain order) outer, ordered axes (grain order) inner/fastest. Only
        // the innermost ordered axis is cumulative (the rest, if any, scan).
        var cellOrder = categorical.Concat(ordered).ToList();
        int cumIdx = cellOrder.FindLastIndex(a => !a.Categorical);
        int stride = 1;
        var axes = new SlabAxisPlan[cellOrder.Count];
        for (int i = cellOrder.Count - 1; i >= 0; i--)
        {
            axes[i] = cellOrder[i] with { Stride = stride, Cumulative = i == cumIdx };
            stride *= cellOrder[i].Cardinality;
        }
        int cells = stride; // product of all cardinalities
        var cumAxis = cumIdx >= 0 ? axes[cumIdx] : null;

        long marks = Convert.ToInt64(db.Scalar($"SELECT count(*) FROM {marksTable}"), CultureInfo.InvariantCulture);
        long nnz = LeafNnz(db, factsTable, companion.GrainChannels);
        double occupancy = marks > 0 && cells > 0 ? (double)nnz / ((double)marks * cells) : 0;
        bool dense = occupancy >= SlabPlan.DenseThreshold;

        // Dense has no entry count to witness facts by, so it always carries a cnt plane (Σ cnt == facts,
        // SLAB-FORMAT §7); the fold ignores planes its measures don't need. Sparse witnesses via nnz.
        var partials = companion.Partials;
        if (dense && !partials.Any(p => p.Kind == PartialKind.Count))
            partials = [.. partials, new Partial(PartialKind.Count)];

        return new SlabPlan
        {
            Dense = dense,
            Cells = cells,
            Occupancy = occupancy,
            Axes = axes,
            Partials = partials,
            CumulativeStride = cumAxis?.Stride ?? 1,
            CumulativeCardinality = cumAxis?.Cardinality ?? 1,
        };
    }

    // Distinct leaf grain rows: one per (real mark, grain values) — the finest grain the leaf companions
    // carry. Equals the source fact count when grain is unique (the reference data).
    private static long LeafNnz(DuckDbSession db, string factsTable, IReadOnlyList<ChannelSpec> grain)
    {
        string cols = string.Concat(grain.Select(g => $", \"{g.Name}\""));
        return Convert.ToInt64(
            db.Scalar($"SELECT count(DISTINCT ({TileSchema.X}, {TileSchema.Y}{cols})) FROM {factsTable}"),
            CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ScanDomain(DuckDbSession db, string factsTable, string channel)
    {
        var values = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT CAST(\"{channel}\" AS VARCHAR) v FROM {factsTable} " +
                          $"WHERE \"{channel}\" IS NOT NULL GROUP BY \"{channel}\" ORDER BY \"{channel}\"";
        using var r = cmd.ExecuteReader();
        while (r.Read()) values.Add(r.GetString(0));
        return values;
    }
}
