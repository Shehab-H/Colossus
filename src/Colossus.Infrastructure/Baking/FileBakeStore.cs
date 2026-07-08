using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Serialization;

namespace Colossus.Infrastructure.Baking;

/// <summary>Filesystem bake store: <c>tiles/&lt;id&gt;/&lt;version&gt;/</c> for tiles + manifest,
/// <c>staging/</c> for extracts, and an atomically flipped <c>latest.json</c> pointer.</summary>
public sealed class FileBakeStore : IBakeStore
{
    private readonly string _tilesRoot;
    private readonly string _stagingRoot;

    public FileBakeStore(string? tilesRoot = null, string? stagingRoot = null)
    {
        _tilesRoot = tilesRoot ?? RepoPaths.TilesDir;
        _stagingRoot = stagingRoot ?? RepoPaths.StagingDir;
    }

    public string StagingPath(string viewId) => Path.Combine(_stagingRoot, viewId + ".parquet");

    public string VersionDirectory(string viewId, string version)
    {
        string dir = Path.Combine(_tilesRoot, viewId, version);
        Directory.CreateDirectory(dir);
        return dir;
    }

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
}
