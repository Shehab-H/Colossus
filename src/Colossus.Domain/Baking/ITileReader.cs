using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

public interface ITileReader
{
    long RowCount(string tilePath);
    bool Exists(string tilePath);
    /// <summary>Rows in one leaf companion block of a packed archive (companion-scale R2) — the block at
    /// <c>[offset, offset+length)</c> decompressed per <paramref name="codec"/> and row-counted.</summary>
    long PackedRowCount(string packPath, long offset, long length, string codec);
    /// <summary>Source facts one slab companion tile witnesses (companion-scale R1, SLAB-FORMAT §7): Σ cnt
    /// when a cnt plane exists, else sparse nnz. −1 when unavailable. <paramref name="planes"/> is the
    /// tile's per-plane byte directory from <see cref="CompanionPack.PlaneEntries"/>; <paramref name="codec"/>
    /// and the optional trained <paramref name="dict"/> decode its blocks (Work Item C).</summary>
    long SlabFacts(string packPath, IReadOnlyDictionary<string, long[]> planes, CompanionSlab slab,
        string codec, byte[]? dict);
    /// <summary>Rows in one packed render tile (tile-transfer Phase 3) — the block at
    /// <c>[offset, offset+length)</c> decompressed per <paramref name="codec"/> (with the trained
    /// <paramref name="dict"/>) and row-counted. Every block in a tile's span carries the same row count, so
    /// the caller passes the geometry block and never inflates the measure planes.</summary>
    long RenderPackedRowCount(string packPath, long offset, long length, string codec, byte[]? dict);
}
