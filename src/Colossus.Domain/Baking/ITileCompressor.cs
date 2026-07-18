namespace Colossus.Domain.Baking;

/// <summary>Writes a transport-precompressed sibling next to every render tile of a baked version, so the
/// serve layer can answer a <c>*.arrow</c> request with <c>Content-Encoding</c> from a static file instead
/// of compressing on the fly (RULES R7). Lossless: a sibling decompresses to its tile byte-for-byte, and the
/// plain tile is always kept (the rollback rail). Companions fetched by HTTP range (facts.pack) are out of
/// scope — <c>Content-Encoding</c> does not compose with ranged requests.</summary>
public interface ITileCompressor
{
    /// <summary>Compress every render tile under <paramref name="versionDirectory"/> into a precompressed
    /// sibling. Idempotent: a sibling already at least as new as its tile is left as-is.</summary>
    TileCompressionStats CompressVersionTiles(string versionDirectory);
}

/// <summary>What one compression pass produced: how many render tiles it (re)compressed and skipped, and the
/// byte totals for the ones it wrote — i.e. the wire savings the serve layer realizes.</summary>
public readonly record struct TileCompressionStats(int Files, int Skipped, long OriginalBytes, long CompressedBytes)
{
    public static readonly TileCompressionStats None = default;

    /// <summary>Compression ratio over the tiles written this pass (1 when nothing was written).</summary>
    public double Ratio => CompressedBytes > 0 ? (double)OriginalBytes / CompressedBytes : 1;
}
