namespace Colossus.Infrastructure.Configuration;

public sealed class TrinoOptions
{
    public const string Section = "Trino";

    public string BaseUrl { get; set; } = "http://localhost:8080";
    public string Catalog { get; set; } = "hive";
    public string Schema { get; set; } = "default";
    public string User { get; set; } = "colossus";
    public string WorkDirectory { get; set; } = "staging/trino-sources";
    public int MaxRetries { get; set; } = 3;
    public long MaxPageBytes { get; set; } = 16 * 1024 * 1024;
    public Dictionary<string, Dictionary<string, string>> HeaderProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void ResolvePaths()
    {
        if (string.IsNullOrWhiteSpace(WorkDirectory))
            throw new InvalidOperationException("The Trino work directory must not be empty.");
        WorkDirectory = Path.GetFullPath(Path.IsPathRooted(WorkDirectory)
            ? WorkDirectory
            : Path.Combine(RepoPaths.Root, WorkDirectory));
        Directory.CreateDirectory(WorkDirectory);
    }
}
