using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace Colossus.Infrastructure.Baking;

/// <summary>Filesystem bake store: <c>tiles/&lt;id&gt;/&lt;version&gt;/</c> for tiles + manifest,
/// <c>staging/</c> for extracts, and an atomically flipped <c>latest.json</c> pointer.</summary>
public sealed class FileBakeStore : IBakeStore, IReleaseArtifactStore
{
    private readonly string _tilesRoot;
    private readonly string _stagingRoot;
    private readonly string _workspacesRoot;
    private readonly string _pendingRoot;

    public FileBakeStore(string? tilesRoot = null, string? stagingRoot = null)
    {
        _tilesRoot = tilesRoot ?? throw new ArgumentNullException(nameof(tilesRoot));
        _stagingRoot = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));
        _workspacesRoot = Path.Combine(_stagingRoot, "workspaces");
        _pendingRoot = Path.Combine(_tilesRoot, ".pending");
    }

    public FileBakeStore(IOptions<StorageOptions> options)
        : this(options.Value.TilesDirectory, options.Value.StagingDirectory)
    {
    }

    public string StagingPath(string viewId, string? attemptId = null)
    {
        string path = attemptId is null
            ? Path.Combine(_stagingRoot, SafeSegment(viewId, nameof(viewId)) + ".parquet")
            : Path.Combine(Workspace(attemptId), "extracts", SafeSegment(viewId, nameof(viewId)) + ".parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public long StagingSize(string viewId, string? attemptId = null)
    {
        string path = StagingPath(viewId, attemptId);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
    public string MarksStagingPath(string viewId, string? attemptId = null)
    {
        string path = attemptId is null
            ? Path.Combine(_stagingRoot, SafeSegment(viewId, nameof(viewId)) + ".marks.parquet")
            : Path.Combine(Workspace(attemptId), "extracts", SafeSegment(viewId, nameof(viewId)) + ".marks.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public string VersionDirectory(string viewId, string version, string? attemptId = null)
    {
        string dir = attemptId is null
            ? PublicVersion(viewId, version)
            : Path.Combine(Workspace(attemptId), "release", SafeSegment(viewId, nameof(viewId)),
                SafeSegment(version, nameof(version)));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string VersionArtifactPath(string viewId, string version, string relativePath) =>
        Under(VersionDirectory(viewId, version), relativePath);

    public long VersionArtifactSize(string viewId, string version, string relativePath) =>
        new FileInfo(VersionArtifactPath(viewId, version, relativePath)).Length;

    public long VersionSize(string viewId, string version) =>
        Directory.EnumerateFiles(VersionDirectory(viewId, version), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    public byte[] ReadVersionArtifact(string viewId, string version, string relativePath) =>
        File.ReadAllBytes(VersionArtifactPath(viewId, version, relativePath));

    public async Task WriteManifestAsync(
        Manifest manifest, string? attemptId = null, CancellationToken ct = default)
    {
        string dir = VersionDirectory(manifest.View.Id, manifest.Version, attemptId);
        string path = Path.Combine(dir, "manifest.json");
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, ColossusJson.Serialize(manifest), ct);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<Manifest?> ReadPreparedManifestAsync(
        string viewId, string version, string attemptId, CancellationToken ct = default)
    {
        string path = Path.Combine(VersionDirectory(viewId, version, attemptId), "manifest.json");
        if (!File.Exists(path)) return null;
        return ColossusJson.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
    }

    public string PreparedManifestHash(string viewId, string version, string attemptId)
    {
        string path = Path.Combine(VersionDirectory(viewId, version, attemptId), "manifest.json");
        return Hash(path);
    }

    public long PreparedArtifactSize(
        string viewId, string version, string attemptId, string relativePath) =>
        new FileInfo(Under(VersionDirectory(viewId, version, attemptId), relativePath)).Length;

    public long PreparedVersionSize(string viewId, string version, string attemptId) =>
        Directory.EnumerateFiles(VersionDirectory(viewId, version, attemptId), "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    public void MakeAddressable(ReleaseArtifactUpload release)
    {
        string preparation = VersionDirectory(release.ViewId, release.Version, release.AttemptId);
        if (!string.Equals(
                Path.GetFullPath(release.SourceDirectory),
                Path.GetFullPath(preparation),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Release upload does not identify this store's preparation directory.");
        if (!File.Exists(Path.Combine(preparation, "manifest.json")) && VersionExists(release.ViewId, release.Version))
        {
            MakeVersionAddressable(release.ViewId, release.Version, release.AttemptId);
            return;
        }
        if (!string.Equals(
                PreparedManifestHash(release.ViewId, release.Version, release.AttemptId),
                release.ManifestHash,
                StringComparison.Ordinal))
            throw new InvalidDataException("Prepared manifest hash changed before publication.");
        MakeVersionAddressable(release.ViewId, release.Version, release.AttemptId);
    }

    public void PublishCurrent(string viewId, string version) => PublishLatest(viewId, version);
    public bool TryReadCurrent(string viewId, out string version) => TryReadLatestVersion(viewId, out version);
    public bool Exists(string viewId, string version) => VersionExists(viewId, version);
    public void Delete(string viewId, string version) => DeleteVersion(viewId, version);
    public string Location(string viewId, string version) => ArtifactLocation(viewId, version);

    public void MakeVersionAddressable(string viewId, string version, string attemptId)
    {
        string source = VersionDirectory(viewId, version, attemptId);
        string manifest = Path.Combine(source, "manifest.json");
        string destination = PublicVersion(viewId, version);
        if (!File.Exists(manifest))
        {
            if (ValidAddressableManifest(destination, viewId, version)) return;
            throw new InvalidDataException($"Prepared release '{viewId}/{version}' has no manifest.");
        }

        string expectedHash = Hash(manifest);
        if (Directory.Exists(destination))
        {
            EnsureSameManifest(destination, expectedHash, viewId, version);
            return;
        }

        string pending = Path.Combine(_pendingRoot, SafeSegment(attemptId, nameof(attemptId)),
            SafeSegment(viewId, nameof(viewId)), SafeSegment(version, nameof(version)));
        Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        if (!Directory.Exists(pending))
            PromoteDirectory(source, pending);
        EnsureSameManifest(pending, expectedHash, viewId, version);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try { Directory.Move(pending, destination); }
        catch (IOException) when (Directory.Exists(destination))
        {
            EnsureSameManifest(destination, expectedHash, viewId, version);
        }
    }

    public void PublishLatest(string viewId, string version)
    {
        if (!VersionExists(viewId, version))
            throw new InvalidOperationException($"Release '{viewId}/{version}' is not addressable.");
        string viewRoot = Path.Combine(_tilesRoot, SafeSegment(viewId, nameof(viewId)));
        Directory.CreateDirectory(viewRoot);
        string tmp = Path.Combine(viewRoot, $"latest.json.tmp-{Guid.NewGuid():N}");
        File.WriteAllText(tmp, ColossusJson.Serialize(new LatestPointer(version)));
        File.Move(tmp, Path.Combine(viewRoot, "latest.json"), overwrite: true);
    }

    public bool TryReadLatestVersion(string viewId, out string version)
    {
        version = "";
        string path = Path.Combine(_tilesRoot, SafeSegment(viewId, nameof(viewId)), "latest.json");
        if (!File.Exists(path)) return false;
        version = ColossusJson.Deserialize<LatestPointer>(File.ReadAllText(path)).Version;
        return true;
    }

    public async Task<Manifest?> ReadManifestAsync(string viewId, string version, CancellationToken ct = default)
    {
        string path = Path.Combine(PublicVersion(viewId, version), "manifest.json");
        if (!File.Exists(path)) return null;
        return ColossusJson.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
    }

    public string TilePath(string viewId, string version, TileId tile) =>
        Path.Combine(_tilesRoot, viewId, version, tile.RelativePath);

    public string CompanionTilePath(string viewId, string version, TileId tile) =>
        Path.ChangeExtension(TilePath(viewId, version, tile), ".facts.arrow");

    public string ArtifactLocation(string viewId, string version) =>
        $"tiles/{SafeSegment(viewId, nameof(viewId))}/{SafeSegment(version, nameof(version))}";

    public bool VersionExists(string viewId, string version) =>
        File.Exists(Path.Combine(PublicVersion(viewId, version), "manifest.json"));

    public void DeleteVersion(string viewId, string version)
    {
        if (TryReadLatestVersion(viewId, out string current) &&
            string.Equals(current, version, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot delete current release '{viewId}/{version}'.");
        string path = PublicVersion(viewId, version);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public IReadOnlyList<BakeWorkspace> ListWorkspaces()
    {
        var values = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        AddWorkspaceDirectories(_workspacesRoot, values);
        AddWorkspaceDirectories(_pendingRoot, values);
        return values.Select(value => new BakeWorkspace(value.Key, value.Value))
            .OrderBy(value => value.LastWriteTime)
            .ToArray();
    }

    public void CleanupStaging(string viewId, string? attemptId = null)
    {
        File.Delete(StagingPath(viewId, attemptId));
        File.Delete(MarksStagingPath(viewId, attemptId));
    }

    public void CleanupWorkspace(string attemptId)
    {
        string workspace = Workspace(attemptId);
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        string pending = Path.Combine(_pendingRoot, SafeSegment(attemptId, nameof(attemptId)));
        if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
    }

    private string Workspace(string attemptId) =>
        Path.Combine(_workspacesRoot, SafeSegment(attemptId, nameof(attemptId)));

    private string PublicVersion(string viewId, string version) =>
        Path.Combine(_tilesRoot, SafeSegment(viewId, nameof(viewId)), SafeSegment(version, nameof(version)));

    private static string SafeSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Path key must be one safe segment.", parameter);
        return value;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureSameManifest(
        string directory, string expectedHash, string viewId, string version)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path) || !string.Equals(Hash(path), expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Addressable release '{viewId}/{version}' conflicts with this attempt.");
    }

    private static bool ValidAddressableManifest(string directory, string viewId, string version)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) return false;
        try
        {
            var manifest = ColossusJson.Deserialize<Manifest>(File.ReadAllText(path));
            return manifest.View.Id == viewId && manifest.Version == version;
        }
        catch { return false; }
    }

    private static void PromoteDirectory(string source, string destination) =>
        PromoteDirectory(source, destination, Directory.Move);

    internal static void PromoteDirectory(
        string source, string destination, Action<string, string> initialMove)
    {
        try
        {
            initialMove(source, destination);
            return;
        }
        catch (IOException) when (Directory.Exists(source) && !Directory.Exists(destination))
        {
            // Path roots do not identify devices on Unix: separate bind mounts both appear under `/`.
            // Copy into a unique sibling on the destination device, then use a same-device atomic move.
        }

        string copy = destination + ".copy-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(source, copy);
            try { Directory.Move(copy, destination); }
            catch (IOException) when (Directory.Exists(destination)) { }
        }
        finally
        {
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: false);
    }

    private static void AddWorkspaceDirectories(
        string root, Dictionary<string, DateTimeOffset> destinations)
    {
        if (!Directory.Exists(root)) return;
        foreach (string path in Directory.EnumerateDirectories(root))
        {
            string id = Path.GetFileName(path);
            var written = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (!destinations.TryGetValue(id, out var current) || written > current)
                destinations[id] = written;
        }
    }

    private static string Under(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Artifact path '{relativePath}' escapes its version directory.");
        return fullPath;
    }
}
