namespace Colossus.Infrastructure.Configuration;

/// <summary>
/// A throughput envelope for one bake. These are soft planning targets: operators trade elapsed time and
/// staging I/O for a smaller hardware footprint; crossing a target is never itself a reason to fail a bake.
/// Zero means use the engine default.
/// </summary>
public sealed class BakeResourceOptions
{
    public const string Section = "BakeResources";

    public int CpuCores { get; set; }
    public int RamMb { get; set; }

    public void Normalize()
    {
        CpuCores = Math.Max(0, CpuCores);
        RamMb = Math.Max(0, RamMb);
        if (RamMb is > 0 and < 256)
            throw new InvalidOperationException(
                $"{Section}:RamMb must be 0 (unrestricted) or at least 256 MB so the runtime and one spillable vector batch can make progress.");
    }

    public long RamBytes => RamMb > 0 ? RamMb * 1024L * 1024L : 0;

    // DuckDB spills supported operators after this target. Leave headroom for Arrow, codecs, HTTP pages,
    // and the runtime rather than handing the entire envelope to the analytical engine.
    public int DuckDbRamMb => RamMb > 0 ? Math.Max(128, RamMb * 3 / 5) : 0;

    // A tile is materialized as Arrow once, and packing/compression can briefly need another copy.
    public long TileWorkingSetBytes => RamBytes > 0 ? Math.Max(16L * 1024 * 1024, RamBytes / 5) : 0;

    // Remote GROUP BY / ORDER BY work should spill early. This is a spill threshold, not a query limit.
    public long SourceSpillThresholdBytes => RamBytes > 0
        ? Math.Max(64L * 1024 * 1024, RamBytes / 4)
        : 1L << 30;
}
