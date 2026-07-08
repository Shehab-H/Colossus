namespace Colossus.Infrastructure;

/// <summary>Resolves shared directories relative to the repo root (the folder holding Colossus.slnx),
/// so tools agree regardless of working directory. Env vars override for real deploys.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string TilesDir =>
        Environment.GetEnvironmentVariable("COLOSSUS_TILES_DIR") ?? Path.Combine(Root, "tiles");

    public static string StagingDir =>
        Environment.GetEnvironmentVariable("COLOSSUS_STAGING_DIR") ?? Path.Combine(Root, "staging");

    public static string ViewsDir =>
        Environment.GetEnvironmentVariable("COLOSSUS_VIEWS_DIR") ?? Path.Combine(Root, "views");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Colossus.slnx")))
                return dir.FullName;
        return Directory.GetCurrentDirectory();
    }
}
