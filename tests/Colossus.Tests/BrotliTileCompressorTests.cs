using System.IO.Compression;
using Colossus.Domain.Baking;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the lossless guarantee (a sibling decompresses to its tile byte-for-byte) and the scope
/// (render tiles only — never a range-fetched companion or the pack).</summary>
public class BrotliTileCompressorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-br-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Write(string relative, byte[] bytes)
    {
        string path = Path.Combine(_dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A spread of byte patterns (compressible runs, structured floats, incompressible noise) so the
    // round-trip isn't accidentally trivial.
    private static byte[] SampleTile(int seed)
    {
        var rng = new Random(seed);
        var bytes = new byte[64 * 1024];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = i % 7 == 0 ? (byte)0 : i % 3 == 0 ? (byte)(i & 0xFF) : (byte)rng.Next(256);
        return bytes;
    }

    private static byte[] Decompress(string siblingPath)
    {
        using var fs = File.OpenRead(siblingPath);
        using var br = new BrotliStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        br.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void CompressFile_Sibling_RoundTripsByteForByte()
    {
        byte[] original = SampleTile(1);
        string tile = Write("5/17/19.arrow", original);

        var (origLen, compLen) = BrotliTileCompressor.CompressFile(tile);

        string sibling = tile + BrotliTileCompressor.SiblingSuffix;
        Assert.True(File.Exists(sibling));
        Assert.Equal(original.Length, origLen);
        Assert.Equal(new FileInfo(sibling).Length, compLen);
        Assert.Equal(original, Decompress(sibling)); // lossless: the sibling IS the tile, compressed
        Assert.False(File.Exists(sibling + ".tmp")); // atomic write left no scratch behind
    }

    [Fact]
    public void CompressTree_CompressesRenderTilesOnly()
    {
        string tileA = Write("5/17/19.arrow", SampleTile(2));
        string tileB = Write("6/1/2.arrow", SampleTile(3));
        string companion = Write("5/17/19.facts.arrow", SampleTile(4)); // range-fetched sibling of the pack
        string pack = Write("facts.pack", SampleTile(5));
        Write("manifest.json", [1, 2, 3]);

        var stats = BrotliTileCompressor.CompressTree(_dir.FullName);

        Assert.Equal(2, stats.Files);
        Assert.True(File.Exists(tileA + ".br"));
        Assert.True(File.Exists(tileB + ".br"));
        Assert.False(File.Exists(companion + ".br")); // *.facts.arrow excluded
        Assert.False(File.Exists(pack + ".br"));       // facts.pack excluded
        Assert.True(stats.CompressedBytes < stats.OriginalBytes);
    }

    [Fact]
    public void CompressFile_IsIdempotent_UnlessForced()
    {
        string tile = Write("0/0/0.arrow", SampleTile(6));

        Assert.True(BrotliTileCompressor.CompressFile(tile).Compressed >= 0); // first pass writes it
        Assert.Equal(-1, BrotliTileCompressor.CompressFile(tile).Compressed); // up-to-date sibling skipped
        Assert.True(BrotliTileCompressor.CompressFile(tile, force: true).Compressed >= 0); // forced recompress
    }

    [Fact]
    public void CompressTree_ReRun_SkipsUpToDateSiblings()
    {
        Write("1/0/0.arrow", SampleTile(7));
        Write("1/0/1.arrow", SampleTile(8));

        Assert.Equal(2, BrotliTileCompressor.CompressTree(_dir.FullName).Files);
        var second = BrotliTileCompressor.CompressTree(_dir.FullName);
        Assert.Equal(0, second.Files);
        Assert.Equal(2, second.Skipped);
    }

    [Fact]
    public void CompressTree_MissingRoot_IsNoOp() =>
        Assert.Equal(TileCompressionStats.None, BrotliTileCompressor.CompressTree(Path.Combine(_dir.FullName, "nope")));
}
