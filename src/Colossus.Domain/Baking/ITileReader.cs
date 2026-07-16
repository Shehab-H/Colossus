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
    /// tile's per-plane byte directory from <see cref="CompanionPack.PlaneEntries"/>.</summary>
    long SlabFacts(string packPath, IReadOnlyDictionary<string, long[]> planes, CompanionSlab slab);
}
