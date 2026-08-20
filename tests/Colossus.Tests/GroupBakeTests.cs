using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Application;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;
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

    private (string OutDir, ReductionResult Result) GroupBake(
        string factsSql,
        ViewConfig? authored = null,
        bool dropFactGeometryAfterGrouping = false)
    {
        authored ??= Authored;
        string factsPath = Path.Combine(_dir.FullName, "facts.parquet");
        string marksPath = Path.Combine(_dir.FullName, "marks.parquet");
        string outDir = Path.Combine(_dir.FullName, "tiles");
        using (var db = DuckDbSession.InMemory())
            db.Exec($"COPY ({factsSql}) TO '{Sql.Path(factsPath)}' (FORMAT PARQUET)");

        var grouping = new DuckDbFactGrouper().GroupToMarks(factsPath, marksPath, authored);
        if (dropFactGeometryAfterGrouping)
        {
            string projected = factsPath + ".projected";
            using (var db = DuckDbSession.InMemory())
                db.Exec($"COPY (SELECT * EXCLUDE (geometry, part_offsets) FROM read_parquet('{Sql.Path(factsPath)}')) " +
                        $"TO '{Sql.Path(projected)}' (FORMAT PARQUET)");
            File.Move(projected, factsPath, overwrite: true);
        }
        var perFact = grouping.PerFactChannels.ToHashSet();
        var grain = authored.Source.Channels
            .Where(c => perFact.Contains(c.Name) && (c.Type == ChannelType.Dict || c.Type == ChannelType.Date))
            .ToList();

        var result = new AggregateReducer().Reduce(new ReductionContext
        {
            StagingParquetPath = marksPath,
            OutputDirectory = outDir,
            Root = new Bbox(0, 0, 4, 4),
            TilePointBudget = 250_000,
            MaxZoom = 8,
            View = EffectiveView.For(authored, grouping),
            GroupRegime = true,
            Companion = new CompanionSpec
            {
                FactsParquetPath = factsPath,
                GrainChannels = grain,
                Partials = MeasurePartials.For(authored.Measures!.Select(m => MeasureParser.Parse(m.Expr))),
            },
        });
        return (outDir, result);
    }

    [Fact]
    public void CompanionFactBatches_AreByteIdenticalToOpenBudget()
    {
        const int marks = 15_625;
        const int rows = marks * 32;
        string factsPath = Path.Combine(_dir.FullName, "batch-source.parquet");
        using (var source = DuckDbSession.InMemory())
            source.Exec($"""
                COPY (
                  SELECT (i % {marks} % 1_000)::FLOAT AS x,
                         floor((i % {marks}) / 1_000)::FLOAT AS y,
                         unhex(lpad(to_hex(i % {marks}), 64, '0')) AS "{MarkKey.GeometryIdentity}",
                         'op-' || CAST(floor(i / {marks}) % 4 AS VARCHAR) AS operator,
                         (floor(i / {marks}) % 8)::USMALLINT AS quarter,
                         (i % 97)::FLOAT AS tests
                  FROM range({rows}) r(i)
                ) TO '{Sql.Path(factsPath)}' (FORMAT PARQUET)
                """);

        var companion = new CompanionSpec
        {
            FactsParquetPath = factsPath,
            GrainChannels =
            [
                new ChannelSpec { Name = "operator", Column = "operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "quarter", Column = "quarter", Role = ChannelRole.Temporal, Type = ChannelType.Date },
            ],
            Partials = [new Partial(PartialKind.Sum, "tests")],
        };

        (string Path, AggregateReducer.CompanionFactsWriteResult Write) Render(string name, BakeResourceOptions? budget)
        {
            string work = Path.Combine(_dir.FullName, name);
            Directory.CreateDirectory(work);
            string output = Path.Combine(work, "facts.parquet");
            using var db = DuckDbSession.OnDisk(work, budget);
            db.Exec($"""
                CREATE TABLE t AS
                SELECT 'p:' || lpad(to_hex(i), 64, '0') AS id,
                       (i % 1_000)::FLOAT AS x,
                       floor(i / 1_000)::FLOAT AS y,
                       3::INTEGER AS zreal
                FROM range({marks}) r(i)
                """);
            return (output, AggregateReducer.LoadCompanionFacts(db, companion, output, budget));
        }

        var open = Render("open", new BakeResourceOptions { RamMb = 0, CpuCores = 2 });
        var bounded = Render("bounded", new BakeResourceOptions { RamMb = 256, CpuCores = 2 });

        Assert.Equal(1, open.Write.BatchCount);
        Assert.True(bounded.Write.BatchCount > 1);
        Assert.Equal(open.Write.Rows, bounded.Write.Rows);
        Assert.Equal(open.Write.LeafNnz, bounded.Write.LeafNnz);
        string openHash = FileHash(open.Path), boundedHash = FileHash(bounded.Path);
        Assert.True(openHash == boundedHash,
            $"open={openHash} ({new FileInfo(open.Path).Length} bytes; {ParquetGroups(open.Path)}), " +
            $"bounded={boundedHash} ({new FileInfo(bounded.Path).Length} bytes; {ParquetGroups(bounded.Path)}), " +
            $"ordered row mismatches={OrderedRowMismatches(open.Path, bounded.Path)}");
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(bounded.Path)!, ".facts-batches-*"));
    }

    [Fact]
    public void CompanionFacts_ReuseMarkZrealWithoutRetainingRepeatedGeometry()
    {
        var (outDir, result) = GroupBake($"""
            SELECT * FROM (VALUES
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1::FLOAT,1::FLOAT, {Ring(1, 1)}, [0,5]::INTEGER[], 'zenith', DATE '2025-04-01',  5::FLOAT, 40::FLOAT),
              (3::FLOAT,3::FLOAT, {Ring(3, 3)}, [0,5]::INTEGER[], 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, geometry, part_offsets, operator, quarter, tests, download_mbps)
            """, dropFactGeometryAfterGrouping: true);

        var slab = ReadSlab(outDir, result, "0/0/0");
        Assert.Equal(3, SlabCompanionReader.Facts(slab, result.CompanionSlab!));
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string ParquetGroups(string path)
    {
        using var db = DuckDbSession.InMemory();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT string_agg(CAST(row_group_num_rows AS VARCHAR), ',') " +
                          $"FROM (SELECT DISTINCT row_group_id, row_group_num_rows FROM parquet_metadata('{Sql.Path(path)}') ORDER BY row_group_id)";
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static long OrderedRowMismatches(string left, string right)
    {
        using var db = DuckDbSession.InMemory();
        return db.Scalar($"""
            WITH a AS (SELECT row_number() OVER () rn, * FROM read_parquet('{Sql.Path(left)}')),
                 b AS (SELECT row_number() OVER () rn, * FROM read_parquet('{Sql.Path(right)}'))
            SELECT count(*) FROM a FULL JOIN b USING (rn)
            WHERE a.x IS DISTINCT FROM b.x OR a.y IS DISTINCT FROM b.y
               OR a."{MarkKey.GeometryIdentity}" IS DISTINCT FROM b."{MarkKey.GeometryIdentity}"
               OR a.operator IS DISTINCT FROM b.operator OR a.quarter IS DISTINCT FROM b.quarter
               OR a.tests IS DISTINCT FROM b.tests OR a.zreal IS DISTINCT FROM b.zreal
            """);
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
        Assert.Equal(2, tileIds.Length);
        Assert.All(tileIds, id =>
        {
            Assert.StartsWith("p:", id);
            Assert.Equal(66, id.Length);
        });

        // The slab lives in facts.pack (no per-tile .facts.arrow file); the manifest-bound directory finds it.
        Assert.False(File.Exists(Path.Combine(outDir, "0/0/0.facts.arrow")));
        var slab = result.CompanionSlab!;
        Assert.Equal("slab", result.CompanionPack!.Format);
        var tileData = ReadSlab(outDir, result, "0/0/0");

        // Σ cnt (or nnz) witnesses every fact reached the slab.
        Assert.Equal(4, SlabCompanionReader.Facts(tileData, slab));

        // Default-context sum(tests) gathers to the two geometry marks: 10+5+3 and 8.
        var byMark = PerMarkTotals(tileData, slab, tile.Length, "sum__tests");
        Assert.Equal(new HashSet<float> { 18f, 8f }, byMark.ToHashSet());
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

    [Fact]
    public void PointGeometry_MeasuresRemainPointTiles_WithImplicitIdentity()
    {
        var pointView = Authored with
        {
            Mark = Mark.Point,
            Source = Authored.Source with
            {
                Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            },
        };
        var (outDir, result) = GroupBake("""
            SELECT * FROM (VALUES
              (1::FLOAT,1::FLOAT, 'apex',   DATE '2025-01-01', 10::FLOAT, 50::FLOAT),
              (1::FLOAT,1::FLOAT, 'zenith', DATE '2025-01-01',  3::FLOAT, 90::FLOAT),
              (3::FLOAT,3::FLOAT, 'zenith', DATE '2025-01-01',  8::FLOAT, 20::FLOAT))
              v(x, y, operator, quarter, tests, download_mbps)
            """, pointView);

        var tile = ReadArrow(Path.Combine(outDir, "0/0/0.arrow"));
        var columns = tile.Schema.FieldsList.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(TileSchema.X, columns);
        Assert.Contains(TileSchema.Y, columns);
        Assert.Contains(TileSchema.Id, columns);
        Assert.DoesNotContain(TileSchema.Geometry, columns);
        Assert.DoesNotContain(TileSchema.PartOffsets, columns);
        Assert.DoesNotContain(MarkKey.GeometryIdentity, columns);
        Assert.Equal(3, SlabCompanionReader.Facts(ReadSlab(outDir, result, "0/0/0"), result.CompanionSlab!));
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
