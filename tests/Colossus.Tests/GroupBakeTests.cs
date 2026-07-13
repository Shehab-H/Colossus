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
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>End-to-end group-regime bake through DuckDB (no ClickHouse): facts → grouper → effective
/// view + companion spec → AggregateReducer. Pins that each render tile gets a fact companion whose
/// rows are the fact partials at grain, keyed by <c>mki</c> — the row index of the fact's mark within
/// the render tile (real mark, or the grid cell it merged into), the client fold's O(1) join.</summary>
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

    private (string OutDir, ReductionResult Result) GroupBake(string factsSql)
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

        var result = new AggregateReducer().Reduce(new ReductionContext
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
        return (outDir, result);
    }

    [Fact]
    public void RealMarks_CompanionPartialsAtGrain_KeyedToTileIds()
    {
        var (outDir, result) = GroupBake($"""
            SELECT * FROM (VALUES
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'apex',   DATE '2025-04-01',  5::FLOAT, 40::FLOAT),
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  3::FLOAT, 90::FLOAT),
              (3::FLOAT,3::FLOAT, {Ring(3, 3)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, geometry, part_offsets, operator, quarter, tests, download_mbps)
            """);

        var tile = ReadArrow(Path.Combine(outDir, "0/0/0.arrow"));
        var tileIds = Ids(tile, TileSchema.Id);
        Assert.Equal(new HashSet<string> { "p:1.0:1.0", "p:3.0:3.0" }, tileIds.ToHashSet());

        // 0/0/0 is a leaf here, so its companion lives as a gzip block in the pack (R2) — the per-file
        // form must be gone and the manifest-bound directory is how the client finds the block.
        Assert.False(File.Exists(Path.Combine(outDir, "0/0/0.facts.arrow")));
        var comp = ReadPacked(outDir, result, "0/0/0");
        Assert.Equal(4, comp.Length); // one row per (mark, operator, quarter)
        var mki = Mki(comp);
        Assert.All(mki, i => Assert.InRange(i, 0, tile.Length - 1)); // every row keys a real tile mark
        // Partials gather to the right mark through mki: p:1:1 has apex Q1 10 + apex Q2 5 + zenith Q1 3,
        // p:3:3 has zenith Q1 8.
        var perMark = SumByMark(comp, "sum__tests", mki, tileIds);
        Assert.Equal(18f, perMark["p:1.0:1.0"], 3);
        Assert.Equal(8f, perMark["p:3.0:3.0"], 3);
        Assert.Equal(1130f, Sum(comp, "swp__download_mbps__tests"), 3); // 500+200+270+160
    }

    [Fact]
    public void MergedMarks_CompanionKeyedByGridCell_PartialsSummedAcrossMarks()
    {
        // Two marks, each with an apex + a zenith fact (so operator is perFact), tiny enough to merge
        // into one ~1px cell at z0. The companion folds both marks' facts into that cell's grain.
        var (outDir, result) = GroupBake($"""
            SELECT * FROM (VALUES
              (1.0000::FLOAT,1.0000::FLOAT, {Tiny(1.0000)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1.0000::FLOAT,1.0000::FLOAT, {Tiny(1.0000)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  3::FLOAT, 90::FLOAT),
              (1.0001::FLOAT,1.0001::FLOAT, {Tiny(1.0001)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 20::FLOAT, 40::FLOAT),
              (1.0001::FLOAT,1.0001::FLOAT, {Tiny(1.0001)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, geometry, part_offsets, operator, quarter, tests, download_mbps)
            """);

        var tile = ReadArrow(Path.Combine(outDir, "0/0/0.arrow"));
        var tileIds = Ids(tile, TileSchema.Id);
        Assert.All(tileIds, id => Assert.StartsWith("g:", id)); // both marks merged into grid cells

        // 0/0/0 is internal (the marks subdivide further), so its companion stays a per-tile file —
        // only leaf companions move into the pack, and the pack never indexes an internal tile.
        Assert.False(result.CompanionPack!.Entries.ContainsKey("0/0/0"));
        var comp = ReadArrow(Path.Combine(outDir, "0/0/0.facts.arrow"));
        Assert.Equal(2, comp.Length); // grain (grid-cell mki, operator)
        Assert.All(Mki(comp), i => Assert.InRange(i, 0, tile.Length - 1));
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

    /// <summary>One leaf companion out of the pack — the directory lookup + bounded gunzip the client mirrors.</summary>
    private static RecordBatch ReadPacked(string outDir, ReductionResult result, string key)
    {
        var pack = result.CompanionPack!;
        Assert.Equal(CompanionPackWriter.Codec, pack.Codec);
        Assert.True(pack.Entries.TryGetValue(key, out var e), $"pack has no entry for {key}");
        using var stream = CompanionPackWriter.ReadBlock(Path.Combine(outDir, pack.File), e![0], e[1]);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    private static string[] Ids(RecordBatch b, string col)
    {
        var a = (StringArray)b.Column(col);
        return [.. Enumerable.Range(0, a.Length).Select(i => a.GetString(i))];
    }

    private static int[] Mki(RecordBatch b)
    {
        var a = (Int32Array)b.Column("mki");
        return [.. Enumerable.Range(0, a.Length).Select(i => a.GetValue(i)!.Value)];
    }

    private static Dictionary<string, float> SumByMark(RecordBatch b, string col, int[] mki, string[] tileIds)
    {
        var a = (FloatArray)b.Column(col);
        var sums = new Dictionary<string, float>();
        for (int i = 0; i < a.Length; i++)
        {
            string id = tileIds[mki[i]];
            sums[id] = sums.GetValueOrDefault(id) + a.GetValue(i)!.Value;
        }
        return sums;
    }

    private static float Sum(RecordBatch b, string col)
    {
        var a = (FloatArray)b.Column(col);
        float s = 0;
        for (int i = 0; i < a.Length; i++) s += a.GetValue(i)!.Value;
        return s;
    }
}
