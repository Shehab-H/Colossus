using Colossus.Domain.Baking;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Tiles;

public sealed class ArrowTileReader : ITileReader
{
    public long RowCount(string tilePath) => ArrowTileWriter.RowCount(tilePath);
    public bool Exists(string tilePath) => File.Exists(tilePath);

    public long PackedRowCount(string packPath, long offset, long length, string codec)
    {
        if (codec != CompanionPackWriter.Codec)
            throw new NotSupportedException($"companion pack codec '{codec}' (this build reads '{CompanionPackWriter.Codec}')");
        return CompanionPackWriter.RowCount(packPath, offset, length);
    }

    public long SlabFacts(string packPath, IReadOnlyDictionary<string, long[]> planes, CompanionSlab slab,
        string codec, byte[]? dict) =>
        SlabCompanionReader.Facts(SlabCompanionReader.Read(packPath, planes, slab, codec, dict), slab);

    public long RenderPackedRowCount(string packPath, long offset, long length, string codec, byte[]? dict)
    {
        if (codec != RenderPackWriter.Codec)
            throw new NotSupportedException($"render pack codec '{codec}' (this build reads '{RenderPackWriter.Codec}')");
        return RenderPackWriter.RowCount(packPath, offset, length, dict);
    }
}
