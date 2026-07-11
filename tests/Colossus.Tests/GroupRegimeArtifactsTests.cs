using Colossus.Application;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.DuckDb;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the two-staging domain assembly: numeric measures from the marks staging; an argmax
/// measure and its perFact filter options from the facts. The crux is that an argmax measure's colour
/// domain is its dimension's FULL domain (so a filter can surface any value), and that the argmax
/// measure and its dimension share one canonical dict order — the render-tile codes and the companion
/// codes then coincide, which is what lets the client fold argmax straight into the tile's colours.</summary>
public class GroupRegimeArtifactsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static ViewConfig Authored => new()
    {
        Id = "mobile-dominance",
        Viewport = Viewport.Geo,
        Mark = Mark.Polygon,
        Source = new SourceSpec
        {
            Query = "SELECT * FROM t",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = new[]
            {
                new ChannelSpec { Name = "operator", Column = "operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "quarter", Column = "quarter", Role = ChannelRole.Temporal, Type = ChannelType.Date },
                new ChannelSpec { Name = "tests", Column = "tests", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            },
        },
        Measures = new[]
        {
            new MeasureSpec { Name = "total_tests", Expr = "sum(tests)" },
            new MeasureSpec { Name = "dominant_operator", Expr = "argmax(operator, sum(tests))" },
        },
    };

    [Fact]
    public void Build_ArgmaxColorsOverFullDimensionDomain_WithAlignedCanonicalCodes()
    {
        string facts = Path.Combine(_dir.FullName, "facts.parquet");
        string marks = Path.Combine(_dir.FullName, "marks.parquet");
        // Only 'apex' ever dominates, but the operator domain is {apex, nova, zenith}. Ring extent 1 over
        // a span-4 root keeps both marks real.
        using (var db = DuckDbSession.InMemory())
            db.Exec($"""
                COPY (SELECT * FROM (VALUES
                  (1::FLOAT,1::FLOAT, [1::FLOAT,1,2,1,2,2,1,2,1,1], [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT),
                  (1::FLOAT,1::FLOAT, [1::FLOAT,1,2,1,2,2,1,2,1,1], [0,5]::INTEGER[], 'nova',   DATE '2025-01-01',  1::FLOAT),
                  (1::FLOAT,1::FLOAT, [1::FLOAT,1,2,1,2,2,1,2,1,1], [0,5]::INTEGER[], 'zenith', DATE '2025-04-01',  2::FLOAT),
                  (2::FLOAT,2::FLOAT, [2::FLOAT,2,3,2,3,3,2,3,2,2], [0,5]::INTEGER[], 'apex',   DATE '2025-01-01',  5::FLOAT),
                  (2::FLOAT,2::FLOAT, [2::FLOAT,2,3,2,3,3,2,3,2,2], [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  1::FLOAT))
                  v(x, y, geometry, part_offsets, operator, quarter, tests))
                TO '{Sql.Path(facts)}' (FORMAT PARQUET)
                """);

        var grouping = new DuckDbFactGrouper().GroupToMarks(facts, marks, Authored);
        var art = GroupRegimeArtifacts.Build(Authored, grouping, facts, marks, new DuckDbChannelDomainScanner());

        var operators = new[] { "apex", "nova", "zenith" }; // scanner sorts ordinal

        // argmax colour domain is the dimension's full domain, not just {apex} (what actually dominates).
        Assert.Equal(operators, art.ChannelDomains["dominant_operator"].Values);
        Assert.Equal(operators, art.ChannelDomains["operator"].Values);       // perFact filter options
        Assert.NotNull(art.ChannelDomains["total_tests"].Min);                // numeric measure from marks
        Assert.NotEmpty(art.ChannelDomains["quarter"].Values!);               // temporal [min,max] bounds

        // Render tile (dominant_operator) and companion (operator) share one canonical order.
        Assert.Equal(operators, art.RenderCanonicalOrders!["dominant_operator"]);
        Assert.Equal(operators, art.Companion.CanonicalDictionaryOrders!["operator"]);

        Assert.Equal(new[] { "operator", "quarter" }, art.GrainChannels);
        Assert.Contains("operator", art.FactChannels.PerFact);
        Assert.True(art.CompanionTilesExpected());
    }
}

file static class GroupArtifactsAssert
{
    // Sugar to keep the assertion readable: a group bake always writes companions.
    public static bool CompanionTilesExpected(this GroupArtifacts a) => a.Companion is not null;
}
