using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

/// <summary>Packs a baked version's render tiles into one per-version archive (tile-transfer Phase 3), each
/// tile's columns becoming independently compressed blocks so a first paint can range geometry + the active
/// colour channel alone. Lossless: a block decompresses to its column byte-for-byte. Unlike
/// <see cref="ITileCompressor"/> this is not additive — a packed version keeps no per-tile render file, so
/// the manifest's <see cref="RenderPack"/> directory is what makes the tiles readable at all.</summary>
public interface ITilePacker
{
    /// <summary>Pack every tile under <paramref name="versionDirectory"/>, deleting the per-tile render files
    /// it packed. <paramref name="firstPaintChannels"/> is the view's role-derived order (colour channel,
    /// then filter slots) that heads each tile's block run. Returns null when nothing could be packed, which
    /// leaves the per-tile files in place and selects the legacy serve path.</summary>
    RenderPack? PackVersion(string versionDirectory, IReadOnlyList<TileMeta> tiles,
        IReadOnlyList<string> firstPaintChannels);
}
