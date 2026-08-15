namespace Colossus.Infrastructure.Configuration;

public sealed class ParquetSourceOptions
{
    public const string Section = "ParquetSources";

    public string WorkDirectory { get; set; } = "staging/parquet-sources";
    public Dictionary<string, ParquetProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void ResolvePaths()
    {
        WorkDirectory = ResolveLocalPath(WorkDirectory);
        Directory.CreateDirectory(WorkDirectory);
        foreach (var provider in Providers.Values)
        {
            if (!provider.Kind.Equals("local", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(provider.BaseLocation)) continue;
            provider.BaseLocation = ResolveLocalPath(provider.BaseLocation);
        }
    }

    private static string ResolveLocalPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException("Parquet source paths must not be empty.");
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(RepoPaths.Root, configuredPath));
    }
}

public sealed class ParquetProviderOptions
{
    public string Kind { get; set; } = "local";
    public string BaseLocation { get; set; } = "datasets/parquet";
    public string? Endpoint { get; set; }
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public string? SessionToken { get; set; }
    public bool UseSsl { get; set; } = true;
    public bool UrlStyle { get; set; }
}
