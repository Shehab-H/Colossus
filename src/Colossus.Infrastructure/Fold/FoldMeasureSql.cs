using Colossus.Domain.Measures;

namespace Colossus.Infrastructure.Fold;

/// <summary>Renders the R4 server fold's measure SQL. The fold is a two-stage aggregation that reproduces
/// the client's companion fold EXACTLY (web/src/lib/measures.ts InnerAgg + slab.ts): stage 1 re-derives the
/// baked f32 grain partials from the facts (<see cref="CellPartialSql"/> — byte-identical to the reducer's
/// <c>PartialSql</c>), stage 2 folds those partials in DOUBLE (<see cref="InnerValueSql"/>) exactly as
/// <c>InnerAgg.value</c> does. Folding the same f32 partials (not the raw facts) is what makes remote and
/// local results bit-for-bit equal — the intermediate per-cell f32 rounding is shared.</summary>
internal static class FoldMeasureSql
{
    // ── stage 1: reproduce a grain cell's partials (matches AggregateReducer.PartialSql byte-for-byte) ──
    public static string CellPartialSql(Partial p) => p.Kind switch
    {
        PartialKind.Sum => $"COALESCE(sum(\"{p.Channel}\"), 0)::FLOAT AS \"{p.Name}\"",
        PartialKind.Count => $"count(*)::INTEGER AS \"{p.Name}\"",
        PartialKind.Swp => $"COALESCE(sum(\"{p.Channel}\" * \"{p.Weight}\"), 0)::FLOAT AS \"{p.Name}\"",
        PartialKind.Min => $"min(\"{p.Channel}\")::FLOAT AS \"{p.Name}\"",
        PartialKind.Max => $"max(\"{p.Channel}\")::FLOAT AS \"{p.Name}\"",
        _ => throw new InvalidOperationException($"unhandled partial {p.Kind}"),
    };

    // ── stage 2: fold the f32 partials into one numeric agg over a group, mirroring InnerAgg.value ──
    // sum/count default to 0 when the (filtered) group is empty (value() returns a[g]); avg/wavg/min/max
    // stay NULL (→ NaN downstream). All sums accumulate the f32 partials promoted to DOUBLE, matching the
    // client's Float64 accumulation over the same f32 cell values.
    public static string InnerValueSql(Agg a, string? filter)
    {
        string f = filter is null ? "" : $" FILTER (WHERE {filter})";
        return a switch
        {
            Sum s => $"COALESCE(sum(\"sum__{s.Channel}\"::DOUBLE){f}, 0)",
            Count => $"COALESCE(sum(\"cnt\"::DOUBLE){f}, 0)",
            Avg av => $"(sum(\"sum__{av.Channel}\"::DOUBLE){f} / nullif(sum(\"cnt\"::DOUBLE){f}, 0))",
            Wavg w => $"(sum(\"swp__{w.Channel}__{w.Weight}\"::DOUBLE){f} / nullif(sum(\"sum__{w.Weight}\"::DOUBLE){f}, 0))",
            Min m => $"min(\"min__{m.Channel}\"){f}",
            Max m => $"max(\"max__{m.Channel}\"){f}",
            _ => throw new InvalidOperationException($"unhandled agg {a.GetType().Name}"),
        };
    }

    /// <summary>A numeric (non-argmax) measure's value over a mark's surviving cells. A plain agg folds with
    /// its own <c>where</c> as a FILTER; a share is <c>COALESCE(restricted,0) / nullif(unrestricted,0)</c>
    /// (the client's <c>shareFinalize</c>).</summary>
    public static string FlatMeasureSql(MeasureExpr e) => e switch
    {
        Agg a => InnerValueSql(a, a.Where is null ? null : WherePred(a.Where.Channel, a.Where.Value)),
        Share sh => $"(COALESCE({InnerValueSql(sh.Inner, WherePred(sh.WhereChannel, sh.WhereValue))}, 0) " +
                    $"/ nullif({InnerValueSql(sh.Inner, null)}, 0))",
        _ => throw new InvalidOperationException($"non-flat measure {e.GetType().Name}"),
    };

    public static string WherePred(string channel, string value) => $"\"{channel}\" = {StrLit(value)}";

    public static string StrLit(string value) => $"'{value.Replace("'", "''")}'";
}
