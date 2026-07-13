using Colossus.Domain.Baking;

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
}
