using System.IO.Compression;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Packs every leaf tile's fact companion into one archive per bake (companion-scale R2), the
/// blocks concatenated in manifest tile order and each gzip-compressed independently, so a client can
/// range-read one tile's block and decompress it alone. The per-file leaf companions are deleted once
/// packed; internal-level companions stay per-tile files (grid-collapsed and bounded, they don't pay
/// the leaf level's per-file overhead). The returned directory rides the manifest and gates the layout.</summary>
public static class CompanionPackWriter
{
    public const string FileName = "facts.pack";
    /// <summary>A browser-native <c>DecompressionStream</c> format — the client decodes with no new dependency.</summary>
    public const string Codec = "gzip";

    public static CompanionPack? Pack(string outputDirectory, IEnumerable<TileMeta> tiles)
    {
        string packPath = Path.Combine(outputDirectory, FileName);
        var entries = new Dictionary<string, long[]>();
        using (var pack = File.Create(packPath))
        {
            foreach (var tile in tiles.Where(t => t.IsLeaf))
            {
                string companion = CompanionPath(outputDirectory, tile);
                if (!File.Exists(companion)) continue;
                long offset = pack.Position;
                using (var gz = new GZipStream(pack, CompressionLevel.Optimal, leaveOpen: true))
                using (var src = File.OpenRead(companion))
                    src.CopyTo(gz);
                entries[$"{tile.Z}/{tile.X}/{tile.Y}"] = [offset, pack.Position - offset];
                File.Delete(companion);
            }
        }
        if (entries.Count > 0)
            return new CompanionPack { File = FileName, Codec = Codec, Entries = entries };
        File.Delete(packPath);
        return null;
    }

    /// <summary>One block decompressed back to its Arrow IPC bytes. The read is bounded to
    /// <paramref name="length"/> before inflating — gzip members are concatenated, and .NET's
    /// <see cref="GZipStream"/> would otherwise run on into the next tile's block.</summary>
    public static MemoryStream ReadBlock(string packPath, long offset, long length)
    {
        byte[] compressed = new byte[length];
        using (var pack = File.OpenRead(packPath))
        {
            pack.Position = offset;
            pack.ReadExactly(compressed);
        }
        var arrow = new MemoryStream();
        using (var gz = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress))
            gz.CopyTo(arrow);
        arrow.Position = 0;
        return arrow;
    }

    public static long RowCount(string packPath, long offset, long length)
    {
        using var stream = ReadBlock(packPath, offset, length);
        using var reader = new ArrowStreamReader(stream);
        long count = 0;
        while (reader.ReadNextRecordBatch() is { } batch)
        {
            count += batch.Length;
            batch.Dispose();
        }
        return count;
    }

    private static string CompanionPath(string outputDirectory, TileMeta tile) =>
        Path.Combine(outputDirectory, $"{tile.Z}/{tile.X}/{tile.Y}.facts.arrow");
}
