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
    public void Write_GeometryTile_TrianglesAreTileGlobal()
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
        var geometry = Assert.IsType<ListArray>(batch.Column("geometry"));
        var triangles = Assert.IsType<ListArray>(batch.Column("triangles"));
        Assert.Equal(2, triangles.Length);

        // Format 2: each row's indices are rebased by its vertex start, so they index the whole tile's
        // coordinate buffer — row 1's indices sit above row 0's vertex count, never overlapping it.
        int vertexBase = 0;
        for (int row = 0; row < 2; row++)
        {
            int rowVertices = ((FloatArray)geometry.GetSlicedValues(row)).Length / 2;
            var indices = (Int32Array)triangles.GetSlicedValues(row);
            Assert.Equal(6, indices.Length); // a quad → 2 triangles
            for (int i = 0; i < indices.Length; i++)
                Assert.InRange(indices.GetValue(i)!.Value, vertexBase, vertexBase + rowVertices - 1);
            vertexBase += rowVertices;
        }
        Assert.Equal(10, vertexBase); // two 5-vertex rings, one contiguous vertex space
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

    [Fact]
    public void Write_DictionaryColumn_RoundTripsCodesAndValues()
    {
        string path = TilePath("dict.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            ArrowTileWriter.Write(db.Connection, """
                SELECT x::FLOAT AS x, x::FLOAT AS y,
                       CASE WHEN x % 3 = 0 THEN 'alpha' WHEN x % 3 = 1 THEN 'beta' ELSE NULL END AS cat
                FROM range(9) r(x)
                """, path, dictionaryColumns: new HashSet<string> { "cat" });
        }

        var batch = ReadSingleBatch(path);
        var col = Assert.IsType<DictionaryArray>(batch.Column("cat"));
        Assert.IsType<Int8Array>(col.Indices); // 2 categories → narrowest index width
        var dict = Assert.IsType<StringArray>(col.Dictionary);
        Assert.Equal(["alpha", "beta"], Enumerable.Range(0, dict.Length).Select(i => dict.GetString(i)));

        var indices = (Int8Array)col.Indices;
        for (int i = 0; i < 9; i++)
        {
            if (i % 3 == 2) Assert.True(indices.IsNull(i));
            else Assert.Equal(i % 3, indices.GetValue(i)!.Value);
        }
    }

    [Fact]
    public void Write_DictionaryColumn_WithCanonicalOrder_CodesAreCanonical()
    {
        string path = TilePath("dict-canon.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            // First-seen order here would be gamma, alpha, beta; the canonical order must win so a row's
            // code is its canonical index (alpha=0, beta=1, gamma=2) and the client needs no remap.
            ArrowTileWriter.Write(db.Connection, """
                SELECT x::FLOAT AS x, x::FLOAT AS y,
                       CASE x % 3 WHEN 0 THEN 'gamma' WHEN 1 THEN 'alpha' ELSE 'beta' END AS cat
                FROM range(6) r(x)
                """, path,
                dictionaryColumns: new HashSet<string> { "cat" },
                canonicalOrders: new Dictionary<string, IReadOnlyList<string>> { ["cat"] = new[] { "alpha", "beta", "gamma" } });
        }

        var batch = ReadSingleBatch(path);
        var col = Assert.IsType<DictionaryArray>(batch.Column("cat"));
        var dict = Assert.IsType<StringArray>(col.Dictionary);
        Assert.Equal(["alpha", "beta", "gamma"], Enumerable.Range(0, dict.Length).Select(i => dict.GetString(i)));

        var indices = (Int8Array)col.Indices;
        Assert.Equal([2, 0, 1, 2, 0, 1], Enumerable.Range(0, 6).Select(i => (int)indices.GetValue(i)!.Value));
    }

    [Fact]
    public void Write_DictionaryColumn_OverCardinalityCap_FallsBackToPlainStrings()
    {
        string path = TilePath("dict-cap.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            // 70_000 distinct values — past the 65_536 cap the exact rows must come back as plain utf8.
            ArrowTileWriter.Write(db.Connection,
                "SELECT x::FLOAT AS x, x::FLOAT AS y, 'v' || x AS wide FROM range(70000) r(x)",
                path, dictionaryColumns: new HashSet<string> { "wide" });
        }

        var batch = ReadSingleBatch(path);
        var col = Assert.IsType<StringArray>(batch.Column("wide"));
        Assert.Equal("v0", col.GetString(0));
        Assert.Equal("v69999", col.GetString(69999));
    }

    [Fact]
    public void Write_ColumnsOutsideTheDictionarySet_StayPlain()
    {
        string path = TilePath("plain.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            ArrowTileWriter.Write(db.Connection,
                "SELECT x::FLOAT AS x, 'name-' || x AS label FROM range(3) r(x)",
                path, dictionaryColumns: new HashSet<string> { "something-else" });
        }

        Assert.IsType<StringArray>(ReadSingleBatch(path).Column("label"));
    }

    [Fact]
    public void Write_Format3_DropsDerivableColumns_AndEncodesGeometry()
    {
        string path = TilePath("f3-rect.arrow");
        using (var db = DuckDbSession.InMemory())
        {
            // Grid-cell tile (the aggregate reducer's ring order) written as tile format 3.
            ArrowTileWriter.Write(db.Connection, """
                SELECT (x + 0.5)::FLOAT AS x, 0.5::FLOAT AS y, ('id-' || x) AS id,
                       [x::FLOAT, 0, x+1, 0, x+1, 1, x::FLOAT, 1, x::FLOAT, 0] AS geometry,
                       [0, 5]::INTEGER[] AS part_offsets,
                       (x * 10)::FLOAT AS value
                FROM range(3) r(x)
                """, path, tileFormat: 3);
        }

        var batch = ReadSingleBatch(path);
        var names = batch.Schema.FieldsList.Select(f => f.Name).ToArray();
        // Derivable/unused columns are gone; the measure stays as a zero-copy column; geom3 carries geometry.
        Assert.Equal(["value", "geom3"], names);
        Assert.Equal(3, batch.Length);
        var value = Assert.IsType<FloatArray>(batch.Column("value"));
        Assert.Equal([0f, 10f, 20f], Enumerable.Range(0, 3).Select(i => value.GetValue(i)!.Value));

        // The whole geometry payload lives in row 0 of the binary column.
        var blob = Assert.IsType<BinaryArray>(batch.Column("geom3"));
        Assert.Equal(GeometryCodec.CodecRect, blob.GetBytes(0)[0]);
        var decoded = GeometryCodec.Decode(blob.GetBytes(0).ToArray());

        var expected = GeometryCodec.BuildFormat2(
        [
            new([0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 0f, 0f], [0, 5]),
            new([1f, 0f, 2f, 0f, 2f, 1f, 1f, 1f, 1f, 0f], [0, 5]),
            new([2f, 0f, 3f, 0f, 3f, 1f, 2f, 1f, 2f, 0f], [0, 5]),
        ]);
        Assert.Equal(expected.Positions, decoded.Positions);
        Assert.Equal(expected.StartIndices, decoded.StartIndices);
        Assert.Equal(expected.Triangles, decoded.Triangles);
    }

    private static RecordBatch ReadSingleBatch(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch() ?? throw new InvalidOperationException("empty tile");
    }
}
