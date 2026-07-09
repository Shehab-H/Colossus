using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Round-trips through a real (in-memory) DuckDB — covers the DbDataReader → Arrow builder
/// mapping and the bake-time triangles column end to end.</summary>
public class ArrowTileWriterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string TilePath(string name) => Path.Combine(_dir.FullName, name);

    [Fact]
    public void Write_ScalarColumns_RoundTrip()
    {
        string path = TilePath("scalar.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            ArrowTileWriter.Write(db.Connection, """
                SELECT x::FLOAT AS x, (x * 2)::FLOAT AS y, x::DOUBLE AS value,
                       x::INTEGER AS i32, x::BIGINT AS i64, 'cat-' || x AS label, DATE '2024-01-01' AS day
                FROM range(5) r(x)
                """, path);
        }

        Assert.Equal(5, ArrowTileWriter.RowCount(path));
        var batch = ReadSingleBatch(path);
        Assert.Equal(["x", "y", "value", "i32", "i64", "label", "day"],
            batch.Schema.FieldsList.Select(f => f.Name));
        var xs = (FloatArray)batch.Column("x");
        Assert.Equal(3f, xs.GetValue(3));
    }

    [Fact]
    public void Write_GeometryTile_GetsValidTrianglesColumn()
    {
        string path = TilePath("poly.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            // Two unit-square cells as closed rings, the canonical polygon tile shape.
            ArrowTileWriter.Write(db.Connection, """
                SELECT (x + 0.5)::FLOAT AS x, 0.5::FLOAT AS y,
                       [x::FLOAT, 0, x+1, 0, x+1, 1, x::FLOAT, 1, x::FLOAT, 0] AS geometry,
                       [0, 5]::INTEGER[] AS part_offsets,
                       (x * 10)::FLOAT AS value
                FROM range(2) r(x)
                """, path);
        }

        var batch = ReadSingleBatch(path);
        var triangles = Assert.IsType<ListArray>(batch.Column("triangles"));
        Assert.Equal(2, triangles.Length);
        for (int row = 0; row < 2; row++)
        {
            var indices = (Int32Array)triangles.GetSlicedValues(row);
            Assert.Equal(6, indices.Length); // a quad → 2 triangles
            for (int i = 0; i < indices.Length; i++)
                Assert.InRange(indices.GetValue(i)!.Value, 0, 4); // row-local vertex indices
        }
    }

    [Fact]
    public void WritePartitioned_SplitsByLeadingTileColumns_AndDropsThem()
    {
        var written = new List<(long Tx, long Ty, long Rows)>();
        using (var db = DuckDbSession.InMemory())
        {
            written = ArrowTileWriter.WritePartitioned(db.Connection, """
                SELECT (x // 10)::BIGINT AS tx, 0::BIGINT AS ty, x::FLOAT AS x, x::FLOAT AS y
                FROM range(25) r(x) ORDER BY tx, ty
                """, (tx, ty) => TilePath($"{tx}-{ty}.arrow"));
        }

        Assert.Equal([(0L, 0L, 10L), (1L, 0L, 10L), (2L, 0L, 5L)], written);
        foreach (var (tx, ty, rows) in written)
        {
            var batch = ReadSingleBatch(TilePath($"{tx}-{ty}.arrow"));
            Assert.Equal(rows, batch.Length);
            Assert.Equal(["x", "y"], batch.Schema.FieldsList.Select(f => f.Name));
        }
    }

    private static RecordBatch ReadSingleBatch(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch() ?? throw new InvalidOperationException("empty tile");
    }
}
