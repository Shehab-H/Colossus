using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Baking;

/// <summary>The bake's persistence boundary: staging + tile locations, manifest writes, and the atomic
/// latest-version flip. Keeps the filesystem out of the use-case layer.</summary>
public interface IBakeStore
{
    string StagingPath(string viewId);
    string MarksStagingPath(string viewId);
    string VersionDirectory(string viewId, string version);
    string VersionArtifactPath(string viewId, string version, string relativePath);
    long VersionArtifactSize(string viewId, string version, string relativePath);
    long VersionSize(string viewId, string version);
    byte[] ReadVersionArtifact(string viewId, string version, string relativePath);
    Task WriteManifestAsync(Manifest manifest, CancellationToken ct = default);
    void PublishLatest(string viewId, string version);

    bool TryReadLatestVersion(string viewId, out string version);
    Task<Manifest?> ReadManifestAsync(string viewId, string version, CancellationToken ct = default);
    string TilePath(string viewId, string version, TileId tile);
    string CompanionTilePath(string viewId, string version, TileId tile);
    void CleanupStaging(string viewId);
}
