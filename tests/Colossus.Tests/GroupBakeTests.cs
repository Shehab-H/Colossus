using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Application;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Reduction;
using Xunit;

namespace Colossus.Tests;

/// <summary>End-to-end group-regime bake through DuckDB (no ClickHouse): facts → grouper → effective
/// view + companion spec → AggregateReducer. Pins that each render tile gets a fact companion whose
/// rows are the fact partials at grain, keyed by an <c>mk</c> that matches the tile's mark ids — real
/// keys while marks are real, the grid-cell key once they merge.</summary>
public class GroupBakeTests : IDisposable
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
                new ChannelSpec { Name = "download_mbps", Column = "download_mbps", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            },
        },
        Measures = new[]
        {
            new MeasureSpec { Name = "total_tests", Expr = "sum(tests)" },
            new MeasureSpec { Name = "avg_download", Expr = "wavg(download_mbps, tests)" },
            new MeasureSpec { Name = "dominant_operator", Expr = "argmax(operator, sum(tests))" },
        },
    };

    private string GroupBake(string factsSql)
    {
        string factsPath = Path.Combine(_dir.FullName, "facts.parquet");
        string marksPath = Path.Combine(_dir.FullName, "marks.parquet");
        string outDir = Path.Combine(_dir.FullName, "tiles");
        using (var db = DuckDbSession.InMemory())
            db.Exec($"COPY ({factsSql}) TO '{Sql.Path(factsPath)}' (FORMAT PARQUET)");

        var grouping = new DuckDbFactGrouper().GroupToMarks(factsPath, marksPath, Authored);
        var perFact = grouping.PerFactChannels.ToHashSet();
        var grain = Authored.Source.Channels
            .Where(c => perFact.Contains(c.Name) && (c.Type == ChannelType.Dict || c.Type == ChannelType.Date))
            .ToList();

        new AggregateReducer().Reduce(new ReductionContext
        {
            StagingParquetPath = marksPath,
            OutputDirectory = outDir,
            Root = new Bbox(0, 0, 4, 4),
            TilePointBudget = 250_000,
            MaxZoom = 8,
            View = EffectiveView.For(Authored, grouping),
            GroupRegime = true,
            Companion = new CompanionSpec
            {
                FactsParquetPath = factsPath,
                GrainChannels = grain,
                Partials = MeasurePartials.For(Authored.Measures!.Select(m => MeasureParser.Parse(m.Expr))),
            },
        });
        return outDir;
    }

    [Fact]
    public void RealMarks_CompanionPartialsAtGrain_KeyedToTileIds()
    {
        string outDir = GroupBake($"""
            SELECT * FROM (VALUES
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'apex',   DATE '2025-04-01',  5::FLOAT, 40::FLOAT),
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  3::FLOAT, 90::FLOAT),
              (3::FLOAT,3::FLOAT, {Ring(3, 3)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, geometry, part_offsets, operator, quarter, tests, download_mbps)
            """);

        var tile = ReadArrow(Path.Combine(outDir, "0/0/0.arrow"));
        var tileIds = Ids(tile, TileSchema.Id);
        Assert.Equal(new HashSet<string> { "p:1.0:1.0", "p:3.0:3.0" }, tileIds);

        var comp = ReadArrow(Path.Combine(outDir, "0/0/0.facts.arrow"));
        Assert.Equal(4, comp.Length); // one row per (mark, operator, quarter)
        Assert.Subset(tileIds, Ids(comp, "mk"));                       // every companion mk is a real tile mark
        Assert.Equal(26f, Sum(comp, "sum__tests"), 3);                // 10+5+3+8
        Assert.Equal(1130f, Sum(comp, "swp__download_mbps__tests"), 3); // 500+200+270+160
    }

    [Fact]
    public void MergedMarks_CompanionKeyedByGridCell_PartialsSummedAcrossMarks()
    {
        // Two marks, each with an apex + a zenith fact (so operator is perFact), tiny enough to merge
        // into one ~1px cell at z0. The companion folds both marks' facts into that cell's grain.
        string outDir = GroupBake($"""
            SELECT * FROM (VALUES
              (1.0000::FLOAT,1.0000::FLOAT, {Tiny(1.0000)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1.0000::FLOAT,1.0000::FLOAT, {Tiny(1.0000)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  3::FLOAT, 90::FLOAT),
              (1.0001::FLOAT,1.0001::FLOAT, {Tiny(1.0001)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 20::FLOAT, 40::FLOAT),
              (1.0001::FLOAT,1.0001::FLOAT, {Tiny(1.0001)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, geometry, part_offsets, operator, quarter, tests, download_mbps)
            """);

        var comp = ReadArrow(Path.Combine(outDir, "0/0/0.facts.arrow"));
        Assert.Equal(2, comp.Length); // grain (grid-cell mk, operator)
        Assert.All(Ids(comp, "mk"), mk => Assert.StartsWith("g:", mk));
        Assert.Equal(41f, Sum(comp, "sum__tests"), 3); // apex 10+20, zenith 3+8, folded across both merged marks
    }

    private static string Ring(double px, double py) =>
        $"[{px}::FLOAT,{py}::FLOAT, {px + 1}::FLOAT,{py}::FLOAT, {px + 1}::FLOAT,{py + 1}::FLOAT, {px}::FLOAT,{py + 1}::FLOAT, {px}::FLOAT,{py}::FLOAT]";

    private static string Tiny(double p) =>
        $"[{p}::FLOAT,{p}::FLOAT, {p + 0.002}::FLOAT,{p}::FLOAT, {p + 0.002}::FLOAT,{p + 0.002}::FLOAT, {p}::FLOAT,{p + 0.002}::FLOAT, {p}::FLOAT,{p}::FLOAT]";

    private static RecordBatch ReadArrow(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    private static HashSet<string> Ids(RecordBatch b, string col)
    {
        var a = (StringArray)b.Column(col);
        return Enumerable.Range(0, a.Length).Select(i => a.GetString(i)).ToHashSet();
    }

    private static float Sum(RecordBatch b, string col)
    {
        var a = (FloatArray)b.Column(col);
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a.GetValue(i)!.Value;
        return s;
    }
}
