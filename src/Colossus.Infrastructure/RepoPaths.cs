namespace Colossus.Infrastructure;

/// <summary>Finds the repo/deployment root used to resolve relative paths supplied by appsettings.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Colossus.slnx")))
                return dir.FullName;
        return AppContext.BaseDirectory;
    }
}
