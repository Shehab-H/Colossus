using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Baking;

/// <summary>The bake's persistence boundary: staging + tile locations, manifest writes, and the atomic
/// latest-version flip. Keeps the filesystem out of the use-case layer.</summary>
public interface IBakeStore
{
    string StagingPath(string viewId, string? attemptId = null);
    long StagingSize(string viewId, string? attemptId = null);
    string MarksStagingPath(string viewId, string? attemptId = null);
    string VersionDirectory(string viewId, string version, string? attemptId = null);
    string VersionArtifactPath(string viewId, string version, string relativePath);
    long VersionArtifactSize(string viewId, string version, string relativePath);
    long VersionSize(string viewId, string version);
    byte[] ReadVersionArtifact(string viewId, string version, string relativePath);
    Task WriteManifestAsync(Manifest manifest, string? attemptId = null, CancellationToken ct = default);
    Task<Manifest?> ReadPreparedManifestAsync(
        string viewId, string version, string attemptId, CancellationToken ct = default);
    long PreparedArtifactSize(string viewId, string version, string attemptId, string relativePath);
    long PreparedVersionSize(string viewId, string version, string attemptId);
    string PreparedManifestHash(string viewId, string version, string attemptId);
    void MakeVersionAddressable(string viewId, string version, string attemptId);
    void PublishLatest(string viewId, string version);

    bool TryReadLatestVersion(string viewId, out string version);
    Task<Manifest?> ReadManifestAsync(string viewId, string version, CancellationToken ct = default);
    string TilePath(string viewId, string version, TileId tile);
    string CompanionTilePath(string viewId, string version, TileId tile);
    string ArtifactLocation(string viewId, string version);
    bool VersionExists(string viewId, string version);
    void DeleteVersion(string viewId, string version);
    IReadOnlyList<BakeWorkspace> ListWorkspaces();
    void CleanupStaging(string viewId, string? attemptId = null);
    void CleanupWorkspace(string attemptId);
}

/// <summary>One private bake workspace. Attempt keys are globally unique, so overlapping or retried
/// runs never share extracts or partially-written releases.</summary>
public sealed record BakeWorkspace(string AttemptId, DateTimeOffset LastWriteTime);

/// <summary>Immutable-release publication boundary. Filesystem and object stores share this contract;
/// callers never rely on rename semantics.</summary>
public interface IReleaseArtifactStore
{
    void MakeAddressable(ReleaseArtifactUpload release);
    void PublishCurrent(string viewId, string version);
    bool TryReadCurrent(string viewId, out string version);
    bool Exists(string viewId, string version);
    void Delete(string viewId, string version);
    string Location(string viewId, string version);
}

public sealed record ReleaseArtifactUpload(
    string ViewId,
    string Version,
    string AttemptId,
    string SourceDirectory,
    string ManifestHash);
