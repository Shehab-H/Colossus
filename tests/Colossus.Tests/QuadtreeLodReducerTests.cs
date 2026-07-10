using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;
using Colossus.Domain.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Reduction;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the LOD merge invariants: an internal node folds only rows sharing a ~1px grid cell
/// (tagging each survivor with merged_count), a pixel-sparse node stays a leaf even over budget, and
/// every source row lands in exactly one leaf.</summary>
public class QuadtreeLodReducerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static ViewConfig View => new()
    {
        Id = "t",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Query = "SELECT 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
        },
    };

    private string Stage(string valuesSql)
    {
        string path = Path.Combine(_dir.FullName, "staging.parquet");
        using var db = DuckDbSession.InMemory();
        db.Exec($"COPY ({valuesSql}) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)");
        return path;
    }

    private (ReductionResult Result, string OutDir) Reduce(string staging, int budget) =>
        Reduce(staging, budget, new Bbox(0, 0, 1, 1), maxZoom: 8);

    private (ReductionResult Result, string OutDir) Reduce(string staging, int budget, Bbox root, int maxZoom)
    {
        string outDir = Path.Combine(_dir.FullName, "tiles");
        var result = new QuadtreeLodReducer().Reduce(new ReductionContext
        {
            StagingParquetPath = staging,
            OutputDirectory = outDir,
            Root = root,
            TilePointBudget = budget,
            MaxZoom = maxZoom,
            View = View,
        });
        return (result, outDir);
    }

    private static RecordBatch ReadTile(string outDir, int z, int x, int y)
    {
        using var stream = File.OpenRead(Path.Combine(outDir, new TileId(z, x, y).RelativePath));
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    [Fact]
    public void DenseNode_MergesPerGridCell_AndTagsMergedCount()
    {
        // Three rows inside one z0 grid cell (cell span = 1/512), two spread far apart.
        string staging = Stage("""
            SELECT * FROM (VALUES
              (0.0005::DOUBLE, 0.0005::DOUBLE, 1::INTEGER),
              (0.0010::DOUBLE, 0.0010::DOUBLE, 2::INTEGER),
              (0.0015::DOUBLE, 0.0015::DOUBLE, 3::INTEGER),
              (0.5::DOUBLE, 0.5::DOUBLE, 4::INTEGER),
              (0.9::DOUBLE, 0.9::DOUBLE, 5::INTEGER)) v(x, y, val)
            """);
        var (result, outDir) = Reduce(staging, budget: 2);

        var root = result.Tiles.Single(t => t is { Z: 0, X: 0, Y: 0 });
        Assert.False(root.IsLeaf);
        Assert.Equal(3, root.Count); // 3 occupied cells, not the budget

        var batch = ReadTile(outDir, 0, 0, 0);
        Assert.Equal(3, batch.Length);
        var merged = Assert.IsType<Int32Array>(batch.Column(TileSchema.MergedCount));
        long representedRows = 0;
        for (int i = 0; i < merged.Length; i++) representedRows += merged.GetValue(i)!.Value;
        Assert.Equal(5, representedRows); // every source row is represented exactly once
        Assert.Contains(3, Enumerable.Range(0, merged.Length).Select(i => merged.GetValue(i)!.Value));

        // The cluster's survivor is the lowest-rowid row — deterministic representative.
        var vals = (Int32Array)batch.Column("val");
        var clusterVal = Enumerable.Range(0, 3).Single(i => merged.GetValue(i) == 3);
        Assert.Equal(1, vals.GetValue(clusterVal));

        Assert.Equal(5, result.LeafPointCount); // nothing dropped: all rows land in leaves
        Assert.Equal(5, result.Tiles.Where(t => t.IsLeaf).Sum(t => t.Count));
    }

    [Fact]
    public void PointOnATileSeam_LandsInExactlyOneLeaf()
    {
        // GeoNames' baked root. 45.04499999999999 is Edge(z=3, i=5) over it — a boundary the old max-edge
        // formula (edgeMin + cell) overshot by an ulp, so tiles 4 and 5 both claimed it on both axes and
        // the seam point was counted in four z=3 tiles instead of one. The two trailing points sit inside
        // one ~1px grid cell, which is what forces the node to subdivide down to the seam.
        var root = new Bbox(-180.17999999999998, -180.17999999999998, 180.17999999999998, 180.17999999999998);
        const double seam = 45.04499999999999;
        string staging = Stage($"""
            SELECT * FROM (VALUES
              ({seam:R}::DOUBLE, {seam:R}::DOUBLE, 1::INTEGER),
              ({seam + 1e-9:R}::DOUBLE, {seam + 1e-9:R}::DOUBLE, 2::INTEGER),
              ({seam + 2e-9:R}::DOUBLE, {seam + 2e-9:R}::DOUBLE, 3::INTEGER)) v(x, y, val)
            """);

        var (result, _) = Reduce(staging, budget: 1, root, maxZoom: 4);

        Assert.Equal(3, result.LeafPointCount);
        Assert.Equal(3, result.Tiles.Where(t => t.IsLeaf).Sum(t => t.Count));
    }

    [Fact]
    public void PixelSparseNode_StaysLeaf_EvenOverBudget()
    {
        string staging = Stage("""
            SELECT * FROM (VALUES
              (0.1::DOUBLE, 0.1::DOUBLE, 1::INTEGER),
              (0.3::DOUBLE, 0.3::DOUBLE, 2::INTEGER),
              (0.5::DOUBLE, 0.5::DOUBLE, 3::INTEGER),
              (0.7::DOUBLE, 0.7::DOUBLE, 4::INTEGER),
              (0.9::DOUBLE, 0.9::DOUBLE, 5::INTEGER)) v(x, y, val)
            """);
        var (result, outDir) = Reduce(staging, budget: 2);

        var root = Assert.Single(result.Tiles); // no collisions → complete at z0, no subdivision
        Assert.True(root.IsLeaf);
        Assert.Equal(5, root.Count);

        var batch = ReadTile(outDir, 0, 0, 0);
        Assert.Equal(5, batch.Length);
        Assert.DoesNotContain(TileSchema.MergedCount, batch.Schema.FieldsList.Select(f => f.Name));
    }
}
