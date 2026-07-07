namespace Colossus.Core;

/// <summary>
/// Resolves shared output directories to the repo root (the folder containing Colossus.slnx) so Bake
/// and Server agree regardless of each tool's working directory. Env vars override for real deploys.
/// </summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string TilesDir =>
        Environment.GetEnvironmentVariable("COLOSSUS_TILES_DIR") ?? Path.Combine(Root, "tiles");

    public static string StagingDir =>
        Environment.GetEnvironmentVariable("COLOSSUS_STAGING_DIR") ?? Path.Combine(Root, "staging");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Colossus.slnx")))
                return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
