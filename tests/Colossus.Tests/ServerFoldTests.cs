using System.Globalization;
using System.Text.Json;
using Colossus.Domain.Measures;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Fold;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the R4 SERVER fold (companion-scale REQUIREMENTS.md R4) to the same frozen semantics the
/// client fold obeys. Two gates:
///
/// (1) <b>Values</b> — the shared cross-language slab fixture (tests/fixtures/slab-cases.json) pins each
/// measure's folded value per mark for six contexts. web/src/lib/slab.test.ts folds it in TS; this folds the
/// identical fixture through the REAL server SQL (<see cref="FoldSql.Tail"/>, the same text
/// <see cref="DuckDbFoldExecutor"/> runs) and asserts the same numbers. If either executor drifts, one side
/// fails.
///
/// (2) <b>Grammar</b> — every expression in tests/fixtures/measure-cases.json compiles through the server's
/// fold renderer, and every error case is rejected by the shared parser, so the remote route accepts exactly
/// the grammar VIEW_CONFIG §4 declares — no more, no less.</summary>
public class ServerFoldTests
{
    private static readonly JsonDocument Slab = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "slab-cases.json")));
    private static readonly JsonDocument Measures = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "measure-cases.json")));

    // The fixture's grain: operator (dict) + quarter (temporal DATE) — what the cells are keyed by.
    private static readonly Dictionary<string, (bool Temporal, string DuckType)> Grain = new(StringComparer.Ordinal)
    {
        ["operator"] = (false, "VARCHAR"),
        ["quarter"] = (true, "DATE"),
    };

    private static List<ResolvedMeasure> FixtureMeasures() =>
        [.. Slab.RootElement.GetProperty("measures").EnumerateArray().Select(m =>
        {
            var ast = MeasureParser.Parse(m.GetProperty("expr").GetString()!);
            return new ResolvedMeasure(m.GetProperty("name").GetString()!, ast, ast is ArgExt);
        })];

    [Fact]
    public void ServerFold_MatchesEveryFixtureContext()
    {
        var measures = FixtureMeasures();
        var partials = MeasurePartials.For(measures.Select(m => m.Ast));
        var folds = Slab.RootElement.GetProperty("folds").EnumerateArray().ToArray();
        Assert.NotEmpty(folds);

        using var db = DuckDbSession.InMemory();
        db.Exec("CREATE TABLE facts (mki INTEGER, operator VARCHAR, quarter DATE, tests FLOAT, download_mbps FLOAT)");
        db.Exec($"INSERT INTO facts VALUES {FactValues()}");

        foreach (var fold in folds)
        {
            var ctx = ParseContext(fold.GetProperty("context"));
            string sql = FixtureSql(measures, partials, ctx);
            var actual = Run(db, sql, measures);

            var expect = fold.GetProperty("expect");
            string label = fold.GetProperty("context").GetRawText();
            foreach (var m in measures)
            {
                var want = expect.GetProperty(m.Name).EnumerateArray().ToArray();
                var got = actual[m.Name];
                Assert.Equal(want.Length, got.Count);
                for (int i = 0; i < want.Length; i++)
                {
                    // fixture: null = NaN / empty mark; 65535 = argmax unknown.
                    if (want[i].ValueKind == JsonValueKind.Null)
                        Assert.True(double.IsNaN(got[i]), $"{m.Name}[{i}] ctx={label}: expected NaN, got {got[i]}");
                    else
                        Assert.Equal(want[i].GetDouble(), got[i], 3);
                }
            }
        }
    }

    [Fact]
    public void ServerFold_CompilesEveryGrammarCase()
    {
        var parse = Measures.RootElement.GetProperty("parse").EnumerateArray().ToArray();
        Assert.NotEmpty(parse);
        foreach (var c in parse)
        {
            string expr = c.GetProperty("expr").GetString()!;
            var ast = MeasureParser.Parse(expr);
            // Every declared expression must render to fold SQL: argmax through the tail's inner-agg path,
            // everything else through the flat path. A grammar the server can't fold is a broken route.
            string sql = ast is ArgExt arg
                ? FoldMeasureSql.InnerValueSql(arg.Inner, null)
                : FoldMeasureSql.FlatMeasureSql(ast);
            Assert.False(string.IsNullOrWhiteSpace(sql), $"no fold SQL for \"{expr}\"");
        }
    }

    [Fact]
    public void ServerFold_RejectsEveryGrammarErrorCase()
    {
        var errors = Measures.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        Assert.NotEmpty(errors);
        foreach (var c in errors)
        {
            string expr = c.GetProperty("expr").GetString()!;
            // The server folds only what the shared parser accepts, so the remote route's grammar is the
            // declared one by construction.
            Assert.Throws<MeasureParseException>(() => MeasureParser.Parse(expr));
        }
    }

    /// <summary>The fixture's facts as `marks` + `cells` (the companion the client would have fetched),
    /// then the REAL fold tail. Tiling is out of scope here — the real-bake parity harness
    /// (web/scripts/bench-fold-route.ts --parity) covers the mki/tiling half against the live client.</summary>
    private static string FixtureSql(List<ResolvedMeasure> measures, IReadOnlyList<Partial> partials, FoldContextDto ctx)
    {
        string cellPartials = string.Concat(partials.Select(p => $", {FoldMeasureSql.CellPartialSql(p)}"));
        string ctxPred = FoldSql.ContextPredicate(ctx, Grain);
        var axes = Slab.RootElement.GetProperty("axes");
        IReadOnlyList<string> domainFor(ArgExt a) =>
            [.. axes.GetProperty(a.Dimension).GetProperty("domain").EnumerateArray().Select(v => v.GetString()!)];

        return $"""
            WITH marks AS (SELECT DISTINCT 0 AS tx, 0 AS ty, mki AS mk, mki FROM facts),
            cells AS (
                SELECT 0 AS tx, 0 AS ty, mki AS mk, "operator", "quarter"{cellPartials}
                FROM facts GROUP BY mki, "operator", "quarter"
            )
            {FoldSql.Tail(measures, ctxPred, domainFor, "'0/0/0'")}
            """;
    }

    private static Dictionary<string, List<double>> Run(DuckDbSession db, string sql, List<ResolvedMeasure> measures)
    {
        var result = measures.ToDictionary(m => m.Name, _ => new List<double>(), StringComparer.Ordinal);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            for (int i = 0; i < measures.Count; i++) // column 0 is the tile key
                result[measures[i].Name].Add(r.IsDBNull(i + 1) ? double.NaN : Convert.ToDouble(r.GetValue(i + 1), CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>The fixture's raw context (as the UI holds it) split into the compiled fold context the
    /// client's buildFoldContext produces: temporal → range, everything else → equality.</summary>
    private static FoldContextDto ParseContext(JsonElement ctx)
    {
        var dto = new FoldContextDto { Equals_ = new(StringComparer.Ordinal), Ranges = new(StringComparer.Ordinal) };
        foreach (var p in ctx.EnumerateObject())
        {
            string v = p.Value.GetString()!;
            if (Grain.TryGetValue(p.Name, out var info) && info.Temporal)
            {
                int i = v.IndexOf("..", StringComparison.Ordinal);
                dto.Ranges[p.Name] = i < 0
                    ? new FoldRangeDto { From = v, To = v }
                    : new FoldRangeDto { From = v[..i], To = v[(i + 2)..] };
            }
            else
            {
                dto.Equals_[p.Name] = v;
            }
        }
        return dto;
    }

    private static string FactValues() => string.Join(",\n", Slab.RootElement.GetProperty("facts").EnumerateArray().Select(f =>
        $"({f.GetProperty("mki").GetInt32()}, '{f.GetProperty("operator").GetString()}', " +
        $"DATE '{f.GetProperty("quarter").GetString()}', {f.GetProperty("tests").GetDouble().ToString(CultureInfo.InvariantCulture)}::FLOAT, " +
        $"{f.GetProperty("download_mbps").GetDouble().ToString(CultureInfo.InvariantCulture)}::FLOAT)"));

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Colossus.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (Colossus.slnx) not found");
    }
}
