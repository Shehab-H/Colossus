using Colossus.Domain.Model;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the leaf companion archive (companion-scale R2): blocks concatenated in tile order,
/// each an independently decompressible gzip member addressed by the returned directory; packed leaf
/// files deleted, internal-level files untouched, and no archive at all when nothing packs.</summary>
public class CompanionPackWriterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-pack-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Companion(int z, int x, int y, byte[] bytes)
    {
        string path = Path.Combine(_dir.FullName, $"{z}/{x}/{y}.facts.arrow");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void PacksLeavesInTileOrder_RoundTripsBlocks_DeletesPackedFiles()
    {
        // Incompressible-ish distinct payloads so offsets and lengths genuinely differ per block.
        var payloads = new Dictionary<string, byte[]>
        {
            ["1/0/0"] = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))],
            ["1/1/0"] = [.. Enumerable.Range(0, 100).Select(i => (byte)i)],
            ["2/2/1"] = [.. Enumerable.Range(0, 999).Select(i => (byte)(i * 31 + 5))],
        };
        foreach (var (key, bytes) in payloads)
        {
            int[] p = [.. key.Split('/').Select(int.Parse)];
            Companion(p[0], p[1], p[2], bytes);
        }
        string internalCompanion = Companion(0, 0, 0, [1, 2, 3]);

        var tiles = new List<TileMeta>
        {
            new(0, 0, 0, 3, IsLeaf: false),
            new(1, 0, 0, 1, IsLeaf: true),
            new(1, 1, 0, 1, IsLeaf: true),
            new(2, 2, 1, 1, IsLeaf: true),
        };
        var pack = CompanionPackWriter.Pack(_dir.FullName, tiles);

        Assert.NotNull(pack);
        Assert.Equal(CompanionPackWriter.Codec, pack!.Codec);
        Assert.Equal(payloads.Keys.ToHashSet(), pack.Entries.Keys.ToHashSet());

        // Blocks sit in tile order, contiguous from 0 — the range directory covers the whole archive.
        string packPath = Path.Combine(_dir.FullName, pack.File);
        long expectedOffset = 0;
        foreach (string key in new[] { "1/0/0", "1/1/0", "2/2/1" })
        {
            long[] e = pack.Entries[key];
            Assert.Equal(expectedOffset, e[0]);
            expectedOffset += e[1];
        }
        Assert.Equal(expectedOffset, new FileInfo(packPath).Length);

        // Every block decompresses alone, byte-identical to the file it replaced; that file is gone.
        foreach (var (key, bytes) in payloads)
        {
            long[] e = pack.Entries[key];
            using var block = CompanionPackWriter.ReadBlock(packPath, e[0], e[1]);
            Assert.Equal(bytes, block.ToArray());
            Assert.False(File.Exists(Path.Combine(_dir.FullName, $"{key}.facts.arrow")));
        }

        Assert.True(File.Exists(internalCompanion)); // internal levels stay per-tile files
    }

    [Fact]
    public void NoLeafCompanions_WritesNoArchive()
    {
        Companion(0, 0, 0, [9, 9]); // internal only
        var pack = CompanionPackWriter.Pack(_dir.FullName,
            [new TileMeta(0, 0, 0, 1, IsLeaf: false), new TileMeta(1, 0, 0, 1, IsLeaf: true)]);

        Assert.Null(pack); // the leaf had no companion file, the internal one is not packable
        Assert.False(File.Exists(Path.Combine(_dir.FullName, CompanionPackWriter.FileName)));
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "0/0/0.facts.arrow")));
    }
}
