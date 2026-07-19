using Colossus.Domain.Baking;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Tiles;

/// <summary>The <see cref="ITilePacker"/> adapter over <see cref="RenderPackWriter"/>.</summary>
public sealed class ArrowTilePacker : ITilePacker
{
    public RenderPack? PackVersion(string versionDirectory, IReadOnlyList<TileMeta> tiles,
        IReadOnlyList<string> firstPaintChannels) =>
        RenderPackWriter.Pack(versionDirectory, tiles, firstPaintChannels);
}
