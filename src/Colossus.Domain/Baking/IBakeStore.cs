using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

/// <summary>The bake's persistence boundary: staging + tile locations, manifest writes, and the atomic
/// latest-version flip. Keeps the filesystem out of the use-case layer.</summary>
public interface IBakeStore
{
    string StagingPath(string viewId);
    string VersionDirectory(string viewId, string version);
    Task WriteManifestAsync(Manifest manifest, CancellationToken ct = default);
    void PublishLatest(string viewId, string version);

    bool TryReadLatestVersion(string viewId, out string version);
    Task<Manifest?> ReadManifestAsync(string viewId, string version, CancellationToken ct = default);
    string TilePath(string viewId, string version, TileId tile);
}
