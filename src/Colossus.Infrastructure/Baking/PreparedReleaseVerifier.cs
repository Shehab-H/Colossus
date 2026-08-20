using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.Baking;

/// <summary>Runs the fidelity witness against the private release directory before it can become
/// addressable. It checks the bytes that will be published, not reducer counters.</summary>
public sealed class PreparedReleaseVerifier(ITileReader tiles, IStagingReader staging)
    : IPreparedReleaseVerifier
{
    public ReleaseVerification Verify(PreparedRelease release)
    {
        Manifest manifest = release.Manifest;
        long leafRows = 0;
        int overBudget = 0;
        long internalCap = Math.Max(
            manifest.TilePointBudget,
            (long)TileSchema.GridPerTile * TileSchema.GridPerTile);
        string renderPackPath = manifest.RenderPack is { } renderPack
            ? Under(release.ReleaseDirectory, renderPack.File)
            : "";
        byte[]? renderDictionary = manifest.RenderPack?.Dict is { } renderDictionaryPath
            ? File.ReadAllBytes(Under(release.ReleaseDirectory, renderDictionaryPath))
            : null;

        foreach (TileMeta tile in manifest.Tiles)
        {
            long rows = RenderRows(release.ReleaseDirectory, tile, manifest.RenderPack,
                renderPackPath, renderDictionary);
            if (rows != tile.Count)
                return ReleaseVerification.Fail(
                    $"tile {tile.Id.RelativePath} has {rows} rows, manifest says {tile.Count}");
            if (tile.IsLeaf) leafRows += rows;
            else if (rows > internalCap) overBudget++;
        }

        if (leafRows != manifest.TotalPoints)
            return ReleaseVerification.Fail(
                $"leaf rows {leafRows:N0} != manifest totalPoints {manifest.TotalPoints:N0}");
        long? stagedRows = staging.Exists(release.StagingPath)
            ? staging.RowCount(release.StagingPath)
            : null;
        if (stagedRows is { } staged && manifest.SourceRows is { } source && staged != source)
            return ReleaseVerification.Fail(
                $"staged extract has {staged:N0} rows, source reported {source:N0}");

        string? failure = manifest.CompanionTiles
            ? VerifyGroup(release, leafRows, stagedRows, overBudget)
            : VerifyRows(leafRows, stagedRows, manifest.SourceRows, overBudget);
        return failure is null ? ReleaseVerification.Pass : ReleaseVerification.Fail(failure);
    }

    private long RenderRows(
        string directory,
        TileMeta tile,
        RenderPack? pack,
        string packPath,
        byte[]? dictionary)
    {
        if (pack is null) return tiles.RowCount(Under(directory, tile.Id.RelativePath));
        string key = $"{tile.Z}/{tile.X}/{tile.Y}";
        if (!pack.Entries.TryGetValue(key, out var groups) ||
            !groups.TryGetValue(RenderPack.GeomGroup, out long[]? geometry))
            throw new InvalidDataException(
                $"render pack has no '{RenderPack.GeomGroup}' block for tile {key}");
        return tiles.RenderPackedRowCount(packPath, geometry[0], geometry[1], pack.Codec, dictionary);
    }

    private static string? VerifyRows(
        long leafRows, long? stagedRows, long? sourceRows, int overBudget)
    {
        long? expected = stagedRows ?? sourceRows;
        if (expected is null) return "release has no independent source-row witness";
        if (leafRows != expected)
            return $"leaf rows {leafRows:N0} != source {expected:N0}";
        return overBudget > 0 ? $"{overBudget} internal tile(s) over the merge-grid cap" : null;
    }

    private string? VerifyGroup(
        PreparedRelease release, long leafRows, long? stagedRows, int overBudget)
    {
        Manifest manifest = release.Manifest;
        if (staging.Exists(release.MarksStagingPath) &&
            staging.RowCount(release.MarksStagingPath) is long marks && leafRows != marks)
            return $"leaf marks {leafRows:N0} != distinct marks {marks:N0}";

        long? source = stagedRows ?? manifest.SourceRows;
        if (source is { } expected)
        {
            CompanionPack? pack = manifest.CompanionPack;
            string packPath = pack is null ? "" : Under(release.ReleaseDirectory, pack.File);
            byte[]? dictionary = pack?.Dict is { } dictionaryPath
                ? File.ReadAllBytes(Under(release.ReleaseDirectory, dictionaryPath))
                : null;
            long companionRows = 0;
            foreach (TileMeta tile in manifest.Tiles.Where(value => value.IsLeaf))
            {
                string key = $"{tile.Z}/{tile.X}/{tile.Y}";
                if (manifest.CompanionSlab is { } slab &&
                    pack?.PlaneEntries is { } planeEntries &&
                    planeEntries.TryGetValue(key, out var planes))
                {
                    long facts = tiles.SlabFacts(packPath, planes, slab, pack.Codec, dictionary);
                    if (facts < 0) { companionRows = -1; break; }
                    companionRows += facts;
                }
                else if (pack is not null && pack.Entries.TryGetValue(key, out long[]? entry))
                {
                    companionRows += tiles.PackedRowCount(packPath, entry[0], entry[1], pack.Codec);
                }
                else
                {
                    string companion = Path.ChangeExtension(
                        Under(release.ReleaseDirectory, tile.Id.RelativePath), ".facts.arrow");
                    if (tiles.Exists(companion)) companionRows += tiles.RowCount(companion);
                }
            }
            if (companionRows >= 0 && companionRows != expected)
                return $"companion facts {companionRows:N0} != source {expected:N0}";
        }
        return overBudget > 0 ? $"{overBudget} internal tile(s) over the merge-grid cap" : null;
    }

    private static string Under(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(fullRoot,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Artifact path '{relative}' escapes the release directory.");
        return path;
    }
}
