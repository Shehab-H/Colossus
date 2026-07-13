namespace Colossus.Domain.Baking;

public interface ITileReader
{
    long RowCount(string tilePath);
    bool Exists(string tilePath);
    /// <summary>Rows in one leaf companion block of a packed archive (companion-scale R2) — the block at
    /// <c>[offset, offset+length)</c> decompressed per <paramref name="codec"/> and row-counted.</summary>
    long PackedRowCount(string packPath, long offset, long length, string codec);
}
