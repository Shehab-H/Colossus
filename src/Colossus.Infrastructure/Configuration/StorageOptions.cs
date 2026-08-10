namespace Colossus.Infrastructure.Configuration;

/// <summary>Filesystem locations shared by the server and bake host. Relative values are resolved from
/// the repository root in development and from the process directory in a standalone deployment.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string TilesDirectory { get; set; } = "tiles";
    public string StagingDirectory { get; set; } = "staging";
    public string LegacyViewsDirectory { get; set; } = "views";
    public string MediaDirectory { get; set; } = "media";

    public void ResolvePaths()
    {
        TilesDirectory = Resolve(TilesDirectory);
        StagingDirectory = Resolve(StagingDirectory);
        LegacyViewsDirectory = Resolve(LegacyViewsDirectory);
        MediaDirectory = Resolve(MediaDirectory);
    }

    public bool HasBake(string viewId) =>
        File.Exists(Path.Combine(TilesDirectory, viewId, "latest.json"));

    private static string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Storage paths must not be empty.");
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(RepoPaths.Root, configuredPath));
    }
}
