using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.Tiles;

/// <summary>The <see cref="ITilePacker"/> adapter over <see cref="RenderPackWriter"/>.
///
/// <para><b>Opt-in until the client reads packs.</b> A packed version keeps no per-tile render file, so a
/// view baked packed cannot render in a browser that still takes the per-tile path — baking one today would
/// silently break that view. Packing therefore stays behind the <c>Rendering:PackTiles</c> setting: the flag
/// is how a scratch view gets baked packed to develop the client against, while real views keep the Phase 1
/// per-file layout. Delete the gate once the client half lands.</para></summary>
public sealed class ArrowTilePacker(IOptions<RenderingOptions> options) : ITilePacker
{
    public RenderPack? PackVersion(string versionDirectory, IReadOnlyList<TileMeta> tiles,
        IReadOnlyList<string> firstPaintChannels) =>
        options.Value.PackTiles ? RenderPackWriter.Pack(versionDirectory, tiles, firstPaintChannels) : null;
}
