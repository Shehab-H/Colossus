using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.Baking;

/// <summary>Filesystem bake store: <c>tiles/&lt;id&gt;/&lt;version&gt;/</c> for tiles + manifest,
/// <c>staging/</c> for extracts, and an atomically flipped <c>latest.json</c> pointer.</summary>
public sealed class FileBakeStore : IBakeStore
{
    private readonly string _tilesRoot;
    private readonly string _stagingRoot;

    public FileBakeStore(string? tilesRoot = null, string? stagingRoot = null)
    {
        _tilesRoot = tilesRoot ?? throw new ArgumentNullException(nameof(tilesRoot));
        _stagingRoot = stagingRoot ?? throw new ArgumentNullException(nameof(stagingRoot));
    }

    public FileBakeStore(IOptions<StorageOptions> options)
        : this(options.Value.TilesDirectory, options.Value.StagingDirectory)
    {
    }

    public string StagingPath(string viewId) => Path.Combine(_stagingRoot, viewId + ".parquet");
    public long StagingSize(string viewId)
    {
        string path = StagingPath(viewId);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
    public string MarksStagingPath(string viewId) => Path.Combine(_stagingRoot, viewId + ".marks.parquet");

    public string VersionDirectory(string viewId, string version)
    {
        string dir = Path.Combine(_tilesRoot, viewId, version);
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

    public async Task WriteManifestAsync(Manifest manifest, CancellationToken ct = default)
    {
        string dir = VersionDirectory(manifest.View.Id, manifest.Version);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), ColossusJson.Serialize(manifest), ct);
    }

    public void PublishLatest(string viewId, string version)
    {
        string viewRoot = Path.Combine(_tilesRoot, viewId);
        Directory.CreateDirectory(viewRoot);
        string tmp = Path.Combine(viewRoot, "latest.json.tmp");
        File.WriteAllText(tmp, ColossusJson.Serialize(new LatestPointer(version)));
        File.Move(tmp, Path.Combine(viewRoot, "latest.json"), overwrite: true);
    }

    public bool TryReadLatestVersion(string viewId, out string version)
    {
        version = "";
        string path = Path.Combine(_tilesRoot, viewId, "latest.json");
        if (!File.Exists(path)) return false;
        version = ColossusJson.Deserialize<LatestPointer>(File.ReadAllText(path)).Version;
        return true;
    }

    public async Task<Manifest?> ReadManifestAsync(string viewId, string version, CancellationToken ct = default)
    {
        string path = Path.Combine(_tilesRoot, viewId, version, "manifest.json");
        if (!File.Exists(path)) return null;
        return ColossusJson.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
    }

    public string TilePath(string viewId, string version, TileId tile) =>
        Path.Combine(_tilesRoot, viewId, version, tile.RelativePath);

    public string CompanionTilePath(string viewId, string version, TileId tile) =>
        Path.ChangeExtension(TilePath(viewId, version, tile), ".facts.arrow");

    public void CleanupStaging(string viewId)
    {
        File.Delete(StagingPath(viewId));
        File.Delete(MarksStagingPath(viewId));
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
