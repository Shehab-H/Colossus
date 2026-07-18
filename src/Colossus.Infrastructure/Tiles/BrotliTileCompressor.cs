using System.IO.Compression;
using Colossus.Domain.Baking;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Brotli precompression of render tiles for HTTP <c>Content-Encoding</c> (tile-transfer initiative,
/// Phase 1). Every <c>z/x/y.arrow</c> gets a <c>z/x/y.arrow.br</c> sibling; the dev server (and Cloudflare R2,
/// see docs/DEPLOY.md) serve the sibling's bytes with <c>Content-Encoding: br</c>, so the wire cost drops ~5x
/// with zero client change — the browser decodes in the network stack, and <c>fetch → arrayBuffer()</c> yields
/// the identical <c>.arrow</c> bytes the zero-copy decode already expects. Static bytes only; nothing is
/// compressed on the request path (RULES R7).
///
/// <para>Excludes <c>*.facts.arrow</c> and <c>facts.pack</c>: the pack is fetched by HTTP range and
/// <c>Content-Encoding</c> does not compose with ranges (its blocks are compressed inside the archive
/// instead).</para>
///
/// <para>Quality 11 / window 24 (the maximum 16 MB window, sized to the worst ~16 MB tile): ~5.5x on large
/// tiles, beating whole-file zstd-19 (~5.1x), at ~23 s for that worst tile — a batch-bake cost, parallelised
/// across cores. Lossless by construction (a round-trip test pins it); the plain tile is never removed.</para></summary>
public sealed class BrotliTileCompressor : ITileCompressor
{
    /// <summary>Sibling suffix appended to a tile path (<c>z/x/y.arrow</c> → <c>z/x/y.arrow.br</c>).</summary>
    public const string SiblingSuffix = ".br";
    private const int Quality = 11; // measured best ratio; the bake is a batch job (see class remarks)
    private const int Window = 24;  // 16 MB — the max window, covering the largest tile whole

    public TileCompressionStats CompressVersionTiles(string versionDirectory) => CompressTree(versionDirectory);

    /// <summary>Compress every render tile under <paramref name="root"/> into a <c>.br</c> sibling, in parallel.
    /// Idempotent: a sibling at least as new as its tile is left as-is unless <paramref name="force"/>. A render
    /// tile is <c>*.arrow</c> excluding <c>*.facts.arrow</c>. Per-tile failures are counted and logged, never
    /// fatal — the plain tile is always the rollback rail.</summary>
    public static TileCompressionStats CompressTree(string root, bool force = false)
    {
        if (!Directory.Exists(root)) return TileCompressionStats.None;
        var tiles = Directory.EnumerateFiles(root, "*.arrow", SearchOption.AllDirectories)
            .Where(IsRenderTile).ToArray();

        long files = 0, skipped = 0, original = 0, compressed = 0, failed = 0;
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, path =>
        {
            try
            {
                var (origLen, compLen) = CompressFile(path, force);
                if (compLen < 0) { Interlocked.Increment(ref skipped); return; }
                Interlocked.Increment(ref files);
                Interlocked.Add(ref original, origLen);
                Interlocked.Add(ref compressed, compLen);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Console.Error.WriteLine($"  brotli: failed to compress {path}: {ex.Message}");
            }
        });
        if (failed > 0)
            Console.Error.WriteLine($"  brotli: {failed} tile(s) failed (plain still served)");
        return new TileCompressionStats((int)files, (int)skipped, original, compressed);
    }

    /// <summary>Compress one <c>.arrow</c> file into <c>{path}.br</c> (written to a temp file, then atomically
    /// renamed so a reader never sees a half-written sibling). Returns the original and compressed byte lengths,
    /// or <c>Compressed = -1</c> when the sibling was already up to date and left untouched.</summary>
    public static (long Original, long Compressed) CompressFile(string arrowPath, bool force = false)
    {
        string sibling = arrowPath + SiblingSuffix;
        var src = new FileInfo(arrowPath);
        if (!force && File.Exists(sibling) && File.GetLastWriteTimeUtc(sibling) >= src.LastWriteTimeUtc)
            return (src.Length, -1);

        byte[] bytes = File.ReadAllBytes(arrowPath);
        byte[] dest = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
        if (!BrotliEncoder.TryCompress(bytes, dest, out int written, Quality, Window))
            throw new IOException($"brotli compression overflowed its buffer for {arrowPath}");

        string tmp = sibling + ".tmp";
        using (var fs = File.Create(tmp))
            fs.Write(dest, 0, written);
        File.Move(tmp, sibling, overwrite: true);
        return (bytes.Length, written);
    }

    // A render tile is z/x/y.arrow. Exclude z/x/y.facts.arrow — a fact companion (a whole-file legacy layout;
    // the current bake packs facts into the range-fetched facts.pack), deliberately left to the pack's codec.
    internal static bool IsRenderTile(string path) =>
        path.EndsWith(".arrow", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".facts.arrow", StringComparison.OrdinalIgnoreCase);
}
