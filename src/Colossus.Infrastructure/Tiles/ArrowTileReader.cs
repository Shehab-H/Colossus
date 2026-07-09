using Colossus.Domain.Baking;

namespace Colossus.Infrastructure.Tiles;

public sealed class ArrowTileReader : ITileReader
{
    public long RowCount(string tilePath) => ArrowTileWriter.RowCount(tilePath);
}
