using System.Text;
using Colossus.Domain.Measures;

namespace Colossus.Infrastructure.Fold;

/// <summary>One requested measure resolved to its AST and output kind (argmax ⇒ u16 codes, else f32).</summary>
internal sealed record ResolvedMeasure(string Name, MeasureExpr Ast, bool IsArgmax);

/// <summary>The fold itself, as SQL over a companion's grain cells — the half of the R4 server fold that
/// carries the frozen measure semantics (VIEW_CONFIG §4), independent of how the cells were produced.
/// <see cref="DuckDbFoldExecutor"/> prepends the tiling CTEs that derive <c>marks</c>/<c>cells</c> from the
/// baked facts; the fixture test (ServerFoldFixtureTests) prepends the shared cross-language fixture's own
/// cells. Both fold through this one text, so the pinned fixture values guard the real query.</summary>
internal static class FoldSql
{
    /// <summary>Argmax code for a mark with no surviving facts (mirrors measures.ts ARGMAX_UNKNOWN).</summary>
    public const int ArgmaxUnknown = 0xFFFF;

    /// <summary>The fold tail, appended to a WITH chain that already defines:
    /// <c>marks(tx, ty, mk, mki)</c> — every mark in the tile, mki-ordered — and
    /// <c>cells(tx, ty, mk, &lt;grain…&gt;, &lt;partials…&gt;)</c> — the grain partials (f32), i.e. the
    /// companion the client would have fetched.
    ///
    /// Survival is structural: only marks with ≥1 context-surviving cell appear in <c>flat</c>, so the LEFT
    /// JOIN turns a mark with no surviving facts into NaN (numeric) / unknown (argmax) — the client's
    /// `survived ? value : NaN`. An agg that is empty *within* a surviving mark keeps its own verb's rule
    /// (sum/count → 0, the rest → NaN), which <see cref="FoldMeasureSql.InnerValueSql"/> encodes.</summary>
    public static string Tail(IReadOnlyList<ResolvedMeasure> measures, string ctxPred,
        Func<ArgExt, IReadOnlyList<string>> domainFor, string tileExpr)
    {
        var flat = measures.Where(m => !m.IsArgmax).ToList();
        string flatCols = string.Concat(flat.Select(m => $", ({FoldMeasureSql.FlatMeasureSql(m.Ast)}) AS \"{m.Name}\""));

        var sb = new StringBuilder();
        sb.Append($"""
            , ctxcells AS (SELECT * FROM cells WHERE {ctxPred})
            , flat AS (SELECT tx, ty, mk{flatCols} FROM ctxcells GROUP BY tx, ty, mk)
            """);

        // argmax/argmin: one inner agg per (mark, dim) over the surviving cells, then the extremising dim.
        // Ties resolve to the LOWEST canonical code, matching the client's strict `v > best` scan over codes
        // in ascending order; a NULL inner value is skipped, as the client skips NaN.
        int ai = 0;
        foreach (var m in measures.Where(m => m.IsArgmax))
        {
            var arg = (ArgExt)m.Ast;
            string domainLit = DomainLiteral(domainFor(arg));
            string dir = arg.IsMax ? "DESC" : "ASC";
            sb.Append($"""
                , arg{ai}_c AS (
                    SELECT tx, ty, mk, (list_position({domainLit}, CAST("{arg.Dimension}" AS VARCHAR)) - 1) AS code,
                           {FoldMeasureSql.InnerValueSql(arg.Inner, null)} AS iv
                    FROM ctxcells GROUP BY tx, ty, mk, "{arg.Dimension}"
                )
                , arg{ai} AS (
                    SELECT tx, ty, mk, code AS "{m.Name}" FROM (
                        SELECT tx, ty, mk, code,
                               row_number() OVER (PARTITION BY tx, ty, mk ORDER BY iv {dir}, code ASC) rn
                        FROM arg{ai}_c WHERE iv IS NOT NULL AND code >= 0
                    ) WHERE rn = 1
                )
                """);
            ai++;
        }

        var outCols = new StringBuilder();
        var argJoins = new StringBuilder();
        ai = 0;
        foreach (var m in measures)
        {
            if (m.IsArgmax)
            {
                outCols.Append($", COALESCE(arg{ai}.\"{m.Name}\", {ArgmaxUnknown}) AS \"{m.Name}\"");
                argJoins.Append($" LEFT JOIN arg{ai} ON arg{ai}.tx = marks.tx AND arg{ai}.ty = marks.ty AND arg{ai}.mk = marks.mk");
                ai++;
            }
            else
            {
                outCols.Append($", COALESCE(CAST(flat.\"{m.Name}\" AS FLOAT), 'nan'::FLOAT) AS \"{m.Name}\"");
            }
        }

        sb.Append($"""

            SELECT {tileExpr} AS tile{outCols}
            FROM marks
            LEFT JOIN flat ON flat.tx = marks.tx AND flat.ty = marks.ty AND flat.mk = marks.mk{argJoins}
            ORDER BY tile, marks.mki
            """);
        return sb.ToString();
    }

    /// <summary>The active context compiled onto the grain cells (VIEW_CONFIG §1): equality on a dict axis,
    /// inclusive range on a temporal axis. A range compares the axis's CANONICAL form (ISO) against the ISO
    /// bounds — lexical on ISO is chronological, which is exactly what the client's <c>resolveBins</c> does
    /// over the same recorded domain, so both routes select the same bins whatever the adapter's date
    /// storage. A context channel absent from the grain can match no cell: the whole fold is impossible
    /// (every mark → NaN / unknown), like the client's `impossible` short-circuit.</summary>
    public static string ContextPredicate(FoldContextDto ctx, Dictionary<string, (bool Temporal, string DuckType)> grain)
    {
        var terms = new List<string>();
        foreach (var (ch, val) in ctx.Equals_ ?? [])
        {
            if (!grain.ContainsKey(ch)) return "false";
            terms.Add(FoldMeasureSql.WherePred(ch, val));
        }
        foreach (var (ch, r) in ctx.Ranges ?? [])
        {
            if (!grain.TryGetValue(ch, out var info)) return "false";
            string iso = Tiles.SlabAxisValue.Sql($"\"{ch}\"", categorical: false, info.Temporal, info.DuckType);
            if (!string.IsNullOrEmpty(r.From)) terms.Add($"{iso} >= {FoldMeasureSql.StrLit(r.From!)}");
            if (!string.IsNullOrEmpty(r.To)) terms.Add($"{iso} <= {FoldMeasureSql.StrLit(r.To!)}");
        }
        return terms.Count == 0 ? "true" : string.Join(" AND ", terms);
    }

    public static string DomainLiteral(IReadOnlyList<string> domain) =>
        "[" + string.Join(", ", domain.Select(v => $"'{v.Replace("'", "''")}'")) + "]";
}
