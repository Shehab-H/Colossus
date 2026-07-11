using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Reduction;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the group-regime tile emission: real marks pass their id and dict channels through;
/// sub-pixel marks merged into one ~1px cell take the grid key as id (matching their fact companion)
/// and the mode of each dict channel, averaging measures. The reducer runs over a synthetic marks
/// parquet — the grouper's output shape — through real DuckDB, no ClickHouse.</summary>
public class AggregateReducerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    // Effective marks view: id + a perMark dict (region) + a numeric measure + an argmax dict measure.
    private static ViewConfig View => new()
    {
        Id = "mobile-dominance",
        Viewport = Viewport.Geo,
        Mark = Mark.Polygon,
        Source = new SourceSpec
        {
            Query = "SELECT 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = new[]
            {
                new ChannelSpec { Name = "id", Column = "id", Role = ChannelRole.Identity, Type = ChannelType.Dict },
                new ChannelSpec { Name = "region", Column = "region", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "total_tests", Column = "total_tests", Role = ChannelRole.Measure, Type = ChannelType.F32 },
                new ChannelSpec { Name = "dominant_operator", Column = "dominant_operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
            },
        },
    };

    private string StageMarks(string valuesSql)
    {
        string path = Path.Combine(_dir.FullName, "marks.parquet");
        using var db = DuckDbSession.InMemory();
        db.Exec($"COPY ({valuesSql}) TO '{Sql.Path(path)}' (FORMAT PARQUET)");
        return path;
    }

    private (ReductionResult Result, string OutDir) Reduce(string marks, Bbox root)
    {
        string outDir = Path.Combine(_dir.FullName, "tiles");
        var result = new AggregateReducer().Reduce(new ReductionContext
        {
            StagingParquetPath = marks,
            OutputDirectory = outDir,
            Root = root,
            TilePointBudget = 250_000,
            MaxZoom = 8,
            View = View,
            GroupRegime = true,
        });
        return (result, outDir);
    }

    // A ~unit ring around (px,py): extent 1 keeps the mark real at z0 over a span-4 root.
    private static string BigRing(double p) =>
        $"[{p}::FLOAT,{p}::FLOAT, {p + 1}::FLOAT,{p}::FLOAT, {p + 1}::FLOAT,{p + 1}::FLOAT, {p}::FLOAT,{p + 1}::FLOAT, {p}::FLOAT,{p}::FLOAT]";

    // A tiny ring (extent 0.002) → sub-pixel at z0, so these marks merge.
    private static string TinyRing(double px, double py) =>
        $"[{px}::FLOAT,{py}::FLOAT, {px + 0.002}::FLOAT,{py}::FLOAT, {px + 0.002}::FLOAT,{py + 0.002}::FLOAT, {px}::FLOAT,{py + 0.002}::FLOAT, {px}::FLOAT,{py}::FLOAT]";

    [Fact]
    public void RealMarks_PassIdAndDictChannelsThrough()
    {
        string marks = StageMarks($"""
            SELECT * FROM (VALUES
              ('mA', 1::FLOAT, 1::FLOAT, {BigRing(1)}, [0,5]::INTEGER[], 'west', 10::FLOAT, 'apex'),
              ('mB', 3::FLOAT, 3::FLOAT, {BigRing(3)}, [0,5]::INTEGER[], 'east', 20::FLOAT, 'zenith'))
              v(id, x, y, geometry, part_offsets, region, total_tests, dominant_operator)
            """);

        var (_, outDir) = Reduce(marks, new Bbox(0, 0, 4, 4));
        var batch = ReadTile(outDir, 0, 0, 0);

        Assert.Equal(2, batch.Length);
        var ids = (StringArray)batch.Column(TileSchema.Id);
        Assert.Equal(new HashSet<string> { "mA", "mB" },
            Enumerable.Range(0, 2).Select(i => ids.GetString(i)).ToHashSet());
        Assert.Contains("apex", Enumerable.Range(0, 2).Select(i => DictAt(batch, "dominant_operator", i)));
        Assert.Contains("west", Enumerable.Range(0, 2).Select(i => DictAt(batch, "region", i)));
        var tests = (FloatArray)batch.Column("total_tests");
        Assert.Equal(new HashSet<float> { 10f, 20f },
            Enumerable.Range(0, 2).Select(i => tests.GetValue(i)!.Value).ToHashSet());
    }

    [Fact]
    public void MergedMarks_TakeGridKeyId_ModeDict_AvgMeasure()
    {
        string marks = StageMarks($"""
            SELECT * FROM (VALUES
              ('mA', 1.0000::FLOAT, 1.0000::FLOAT, {TinyRing(1.0000, 1.0000)}, [0,5]::INTEGER[], 'west', 10::FLOAT, 'apex'),
              ('mB', 1.0001::FLOAT, 1.0001::FLOAT, {TinyRing(1.0001, 1.0001)}, [0,5]::INTEGER[], 'west', 20::FLOAT, 'apex'),
              ('mC', 1.0002::FLOAT, 1.0002::FLOAT, {TinyRing(1.0002, 1.0002)}, [0,5]::INTEGER[], 'east', 30::FLOAT, 'zenith'))
              v(id, x, y, geometry, part_offsets, region, total_tests, dominant_operator)
            """);

        var (_, outDir) = Reduce(marks, new Bbox(0, 0, 4, 4));
        var batch = ReadTile(outDir, 0, 0, 0);

        Assert.Equal(1, batch.Length); // all three collapsed into one ~1px grid cell
        var id = ((StringArray)batch.Column(TileSchema.Id)).GetString(0);
        Assert.StartsWith("g:", id);                                   // synthetic grid key, not a real mark id
        Assert.Equal("apex", DictAt(batch, "dominant_operator", 0));   // mode(apex, apex, zenith)
        Assert.Equal("west", DictAt(batch, "region", 0));              // mode(west, west, east)
        Assert.Equal(20f, ((FloatArray)batch.Column("total_tests")).GetValue(0)!.Value, 3); // avg(10,20,30)
    }

    private static RecordBatch ReadTile(string outDir, int z, int x, int y)
    {
        using var stream = File.OpenRead(Path.Combine(outDir, new TileId(z, x, y).RelativePath));
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    private static string DictAt(RecordBatch b, string col, int i)
    {
        var d = (DictionaryArray)b.Column(col);
        var dict = (StringArray)d.Dictionary;
        int code = d.Indices switch
        {
            Int8Array a => a.GetValue(i)!.Value,
            Int16Array a => a.GetValue(i)!.Value,
            Int32Array a => a.GetValue(i)!.Value,
            _ => throw new InvalidOperationException("unexpected dictionary index width"),
        };
        return dict.GetString(code);
    }
}
