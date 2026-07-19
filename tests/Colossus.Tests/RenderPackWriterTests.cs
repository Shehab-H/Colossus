using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>The render pack must be lossless (every column reads back bit-for-bit through its block) and
/// must lay a tile's blocks down in first-paint order, because that ordering is what makes the default
/// paint one contiguous range instead of a scatter of small ones.</summary>
public class RenderPackWriterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-pack-");

    public void Dispose() => _dir.Delete(recursive: true);

    // A format-3 polygon tile: encoded geom3 payload + two measure columns + a dict column.
    private void WriteTile(TileMeta tile, int rows = 6)
    {
        string path = Path.Combine(_dir.FullName, new TileId(tile.Z, tile.X, tile.Y).RelativePath);
        using var db = DuckDbSession.InMemory();
        ArrowTileWriter.Write(db.Connection, $"""
            SELECT (x + 0.5)::FLOAT AS x, 0.5::FLOAT AS y,
                   [x::FLOAT, 0, x+1, 0, x+1, 1, x::FLOAT, 1, x::FLOAT, 0] AS geometry,
                   [0, 5]::INTEGER[] AS part_offsets,
                   (x * 10.5)::FLOAT AS value,
                   (x * -3.25)::FLOAT AS speed,
                   'cat-' || (x % 3) AS category
            FROM range({rows}) r(x)
            """, path, dictionaryColumns: new HashSet<string> { "category" }, tileFormat: 3);
    }

    private static RecordBatch ReadBlock(string packPath, long[] range, byte[]? dict)
    {
        using var stream = RenderPackWriter.ReadBlock(packPath, range[0], range[1], dict);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    [Fact]
    public void Packs_every_column_and_removes_the_per_tile_files()
    {
        var tile = new TileMeta(3, 1, 2, 6, true);
        WriteTile(tile);
        string tilePath = Path.Combine(_dir.FullName, new TileId(3, 1, 2).RelativePath);
        File.WriteAllBytes(tilePath + ".br", [1, 2, 3]); // a phase-1 sibling must go too

        var pack = RenderPackWriter.Pack(_dir.FullName, [tile], ["value", "category"]);

        Assert.NotNull(pack);
        Assert.False(File.Exists(tilePath), "per-tile .arrow must not survive a packed bake");
        Assert.False(File.Exists(tilePath + ".br"), "per-tile .arrow.br must not survive a packed bake");
        Assert.True(File.Exists(Path.Combine(_dir.FullName, RenderPackWriter.FileName)));

        var groups = pack.Entries["3/1/2"];
        Assert.Equal(
            [RenderPack.GeomGroup, "value", "category", "speed"],
            groups.OrderBy(g => g.Value[0]).Select(g => g.Key));
    }

    [Fact]
    public void First_paint_groups_are_one_contiguous_run_at_the_head_of_the_tile()
    {
        var tile = new TileMeta(3, 1, 2, 6, true);
        WriteTile(tile);

        var pack = RenderPackWriter.Pack(_dir.FullName, [tile], ["value", "category"])!;
        Assert.Equal([RenderPack.GeomGroup, "value", "category"], pack.FirstPaint);

        var groups = pack.Entries["3/1/2"];
        // The first-paint groups must be adjacent and start the tile's span, so one Range covers them.
        long cursor = groups[RenderPack.GeomGroup][0];
        foreach (string g in pack.FirstPaint)
        {
            Assert.Equal(cursor, groups[g][0]);
            cursor += groups[g][1];
        }
        // ...and everything else follows, so a whole-tile read is still one range.
        foreach (var (name, range) in groups.Where(g => !pack.FirstPaint.Contains(g.Key)))
            Assert.True(range[0] >= cursor, $"lazy group '{name}' must sit after the first-paint run");
    }

    [Fact]
    public void Columns_read_back_bit_for_bit_through_their_blocks()
    {
        var tile = new TileMeta(3, 1, 2, 6, true);
        string tilePath = Path.Combine(_dir.FullName, new TileId(3, 1, 2).RelativePath);
        WriteTile(tile);

        // Ground truth: the tile exactly as format 3 wrote it, before packing.
        var expected = new Dictionary<string, float[]>();
        byte[] expectedGeom3;
        using (var stream = File.OpenRead(tilePath))
        using (var reader = new ArrowStreamReader(stream))
        using (var batch = reader.ReadNextRecordBatch()!)
        {
            foreach (string name in new[] { "value", "speed" })
                expected[name] = ((FloatArray)batch.Column(name)).Values.ToArray();
            expectedGeom3 = ((BinaryArray)batch.Column("geom3")).GetBytes(0).ToArray();
        }

        var pack = RenderPackWriter.Pack(_dir.FullName, [tile], ["value"])!;
        string packPath = Path.Combine(_dir.FullName, pack.File);
        byte[]? dict = pack.Dict is null ? null : File.ReadAllBytes(Path.Combine(_dir.FullName, pack.Dict));
        var groups = pack.Entries["3/1/2"];

        using (var geom = ReadBlock(packPath, groups[RenderPack.GeomGroup], dict))
            Assert.Equal(expectedGeom3, ((BinaryArray)geom.Column("geom3")).GetBytes(0).ToArray());

        foreach (var (name, values) in expected)
            using (var block = ReadBlock(packPath, groups[name], dict))
                Assert.Equal(values, ((FloatArray)block.Column(name)).Values.ToArray());
    }

    [Fact]
    public void Row_count_reads_through_the_geometry_block_alone()
    {
        var tile = new TileMeta(3, 1, 2, 6, true);
        WriteTile(tile, rows: 6);

        var pack = RenderPackWriter.Pack(_dir.FullName, [tile], ["value"])!;
        string packPath = Path.Combine(_dir.FullName, pack.File);
        byte[]? dict = pack.Dict is null ? null : File.ReadAllBytes(Path.Combine(_dir.FullName, pack.Dict));
        var geom = pack.Entries["3/1/2"][RenderPack.GeomGroup];

        Assert.Equal(6, RenderPackWriter.RowCount(packPath, geom[0], geom[1], dict));
    }

    [Fact]
    public void Multiple_tiles_each_keep_their_own_contiguous_span()
    {
        TileMeta[] tiles = [new(3, 1, 2, 6, true), new(3, 1, 3, 6, true), new(4, 2, 5, 6, true)];
        foreach (var t in tiles) WriteTile(t);

        var pack = RenderPackWriter.Pack(_dir.FullName, tiles, ["value"])!;
        Assert.Equal(3, pack.Entries.Count);

        // Each tile's blocks occupy one uninterrupted span — a whole-tile fetch is a single range.
        var spans = pack.Entries.Select(e =>
        {
            long start = e.Value.Min(g => g.Value[0]);
            long end = e.Value.Max(g => g.Value[0] + g.Value[1]);
            long bytes = e.Value.Sum(g => g.Value[1]);
            Assert.Equal(bytes, end - start);
            return (start, end);
        }).OrderBy(s => s.start).ToList();

        for (int i = 1; i < spans.Count; i++)
            Assert.True(spans[i].start >= spans[i - 1].end, "tile spans must not interleave");
    }
}
