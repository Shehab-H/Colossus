using Colossus.Domain.Baking;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Tiles;

/// <summary>The <see cref="ITilePacker"/> adapter over <see cref="RenderPackWriter"/>.
///
/// <para><b>Opt-in until the client reads packs.</b> A packed version keeps no per-tile render file, so a
/// view baked packed cannot render in a browser that still takes the per-tile path — baking one today would
/// silently break that view. Packing therefore stays behind <c>COLOSSUS_RENDER_PACK=1</c>: the flag is how a
/// scratch view gets baked packed to develop the client against, while the real views keep the Phase 1
/// per-file layout. Delete the gate once the client half lands.</para></summary>
public sealed class ArrowTilePacker : ITilePacker
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("COLOSSUS_RENDER_PACK") == "1";

    public RenderPack? PackVersion(string versionDirectory, IReadOnlyList<TileMeta> tiles,
        IReadOnlyList<string> firstPaintChannels) =>
        Enabled ? RenderPackWriter.Pack(versionDirectory, tiles, firstPaintChannels) : null;
}
