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
/// view + companion spec → AggregateReducer, now emitting a slab companion (companion-scale R1). Pins that
/// each render tile's slab reconstructs the right per-mark partials at grain and that the fact witness
/// (Σ cnt / nnz) holds. The exact slab encoding is pinned separately by <see cref="SlabFormatTests"/>.</summary>
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
    public void RealMarks_SlabPartialsAtGrain_KeyedToTileMarks()
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

        // The slab lives in facts.pack (no per-tile .facts.arrow file); the manifest-bound directory finds it.
        Assert.False(File.Exists(Path.Combine(outDir, "0/0/0.facts.arrow")));
        var slab = result.CompanionSlab!;
        Assert.Equal("slab", result.CompanionPack!.Format);
        var tileData = ReadSlab(outDir, result, "0/0/0");

        // Σ cnt (or nnz) witnesses every fact reached the slab.
        Assert.Equal(4, SlabCompanionReader.Facts(tileData, slab));

        // Default-context sum(tests) gathers to the right mark: p:1:1 = 10+5+3, p:3:3 = 8.
        var byMark = PerMarkTotals(tileData, slab, tile.Length, "sum__tests");
        Assert.Equal(18f, byMark[System.Array.IndexOf(tileIds, "p:1.0:1.0")], 3);
        Assert.Equal(8f, byMark[System.Array.IndexOf(tileIds, "p:3.0:3.0")], 3);
        // Σ swp over the whole tile = 500+200+270+160.
        Assert.Equal(1130f, PerMarkTotals(tileData, slab, tile.Length, "swp__download_mbps__tests").Sum(), 3);
    }

    [Fact]
    public void MergedMarks_SlabKeyedByGridCell_PartialsSummedAcrossMarks()
    {
        // Two marks, each with an apex + a zenith fact, tiny enough to merge into one ~1px cell at z0.
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

        // All levels (leaf and internal) are packed now — the internal 0/0/0 slab lives in facts.pack.
        var slab = result.CompanionSlab!;
        var tileData = ReadSlab(outDir, result, "0/0/0");
        Assert.Equal(4, SlabCompanionReader.Facts(tileData, slab)); // apex 2 + zenith 2 facts merged
        // sum(tests) folded across both merged marks: apex 10+20, zenith 3+8 → total 41.
        Assert.Equal(41f, PerMarkTotals(tileData, slab, tile.Length, "sum__tests").Sum(), 3);
    }

    // Default-context (all cells) per-mark total of a subtractable partial, layout-aware: sparse sums a
    // mark's entries; dense sums the cumulative last-bin of each categorical run (its run total).
    private static float[] PerMarkTotals(SlabTile t, CompanionSlab slab, int markCount, string partial)
    {
        var plane = t.FloatPlanes[partial];
        var res = new float[markCount];
        if (!t.Dense)
        {
            for (int m = 0; m < markCount; m++)
                for (int e = t.Offsets![m]; e < t.Offsets[m + 1]; e++) res[m] += plane[e];
            return res;
        }
        int cells = slab.Cells;
        int idx = -1;
        for (int i = 0; i < slab.Axes.Count; i++) if (slab.Axes[i].Cumulative) idx = i;
        int stride = 1;
        for (int j = idx + 1; j < slab.Axes.Count; j++) stride *= slab.Axes[j].Cardinality;
        int card = idx >= 0 ? slab.Axes[idx].Cardinality : 1;
        for (int c = 0; c < cells; c++)
            if (idx < 0 || c / stride % card == card - 1)
                for (int m = 0; m < markCount; m++) res[m] += plane[c * markCount + m];
        return res;
    }

    private static SlabTile ReadSlab(string outDir, ReductionResult result, string key)
    {
        var pack = result.CompanionPack!;
        Assert.True(pack.PlaneEntries!.TryGetValue(key, out var planes), $"pack has no plane directory for {key}");
        byte[]? dict = pack.Dict is { } d ? File.ReadAllBytes(Path.Combine(outDir, d)) : null;
        return SlabCompanionReader.Read(Path.Combine(outDir, pack.File), planes!, result.CompanionSlab!, pack.Codec, dict);
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

    private static string[] Ids(RecordBatch b, string col)
    {
        var a = (StringArray)b.Column(col);
        return [.. Enumerable.Range(0, a.Length).Select(i => a.GetString(i))];
    }
}
