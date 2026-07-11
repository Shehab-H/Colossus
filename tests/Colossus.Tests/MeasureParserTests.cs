using System.Text.Json;
using System.Text.Json.Nodes;
using Colossus.Domain.Measures;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the measure grammar (VIEW_CONFIG §4) to one spec across C# and the web mirror. The
/// shared fixture <c>tests/fixtures/measure-cases.json</c> (also read by <c>measures.test.ts</c>)
/// lists expr→AST parse cases and expr→message error cases; if either parser drifts, this fails.</summary>
public class MeasureParserTests
{
    private sealed record Fixture(IReadOnlyList<ParseCase> Parse, IReadOnlyList<ErrorCase> Errors);
    private sealed record ParseCase(string Expr, JsonElement Ast);
    private sealed record ErrorCase(string Expr, string Message);

    private static Fixture Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "measure-cases.json");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), opts)!;
    }

    [Fact]
    public void Parse_MatchesEveryFixtureCase()
    {
        var f = Load();
        Assert.NotEmpty(f.Parse);
        foreach (var c in f.Parse)
        {
            var expected = JsonNode.Parse(c.Ast.GetRawText());
            var actual = Describe(MeasureParser.Parse(c.Expr));
            Assert.True(JsonNode.DeepEquals(expected, actual),
                $"expr \"{c.Expr}\" → {actual.ToJsonString()}, fixture {expected!.ToJsonString()}");
        }
    }

    [Fact]
    public void Parse_RejectsEveryFixtureErrorCase()
    {
        var f = Load();
        Assert.NotEmpty(f.Errors);
        foreach (var c in f.Errors)
        {
            var ex = Assert.Throws<MeasureParseException>(() => MeasureParser.Parse(c.Expr));
            Assert.Contains(c.Message, ex.Message);
        }
    }

    private static JsonObject Describe(MeasureExpr e) => e switch
    {
        Sum s => Agg("sum", s.Channel, null, s.Where),
        Count c => Agg("count", null, null, c.Where),
        Avg a => Agg("avg", a.Channel, null, a.Where),
        Wavg w => Agg("wavg", w.Channel, w.Weight, w.Where),
        Min m => Agg("min", m.Channel, null, m.Where),
        Max m => Agg("max", m.Channel, null, m.Where),
        Share sh => new JsonObject
        {
            ["kind"] = "share",
            ["inner"] = Describe(sh.Inner),
            ["whereChannel"] = sh.WhereChannel,
            ["whereValue"] = sh.WhereValue,
        },
        ArgExt ax => new JsonObject
        {
            ["kind"] = ax.IsMax ? "argmax" : "argmin",
            ["dimension"] = ax.Dimension,
            ["inner"] = Describe(ax.Inner),
        },
        _ => throw new InvalidOperationException($"unhandled node {e.GetType().Name}"),
    };

    private static JsonObject Agg(string kind, string? channel, string? weight, WhereClause? where)
    {
        var o = new JsonObject { ["kind"] = kind };
        if (channel is not null) o["channel"] = channel;
        if (weight is not null) o["weight"] = weight;
        if (where is not null)
            o["where"] = new JsonObject { ["channel"] = where.Channel, ["value"] = where.Value };
        return o;
    }
}
