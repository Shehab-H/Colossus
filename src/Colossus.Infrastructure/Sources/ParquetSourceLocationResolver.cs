using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;

namespace Colossus.Infrastructure.Sources;

internal enum ParquetProviderKind { Local, Http, S3 }

internal sealed record ResolvedParquetSource(
    string ProviderAlias,
    ParquetProviderKind Kind,
    ParquetProviderOptions Provider,
    IReadOnlyList<string> ReadLocations,
    IReadOnlyList<string> LogicalInputs,
    string Fingerprint)
{
    public SourceInputSnapshot Snapshot => new() { Inputs = LogicalInputs, Fingerprint = Fingerprint };
}

internal sealed class ParquetSourceLocationResolver(
    ParquetSourceOptions options,
    IHttpClientFactory httpClients)
{
    private const int MaximumResolvedFiles = 10_000;

    public IReadOnlyList<string> ProviderAliases => options.Providers.Keys
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public async Task<ResolvedParquetSource> ResolveAsync(
        IReadOnlyDictionary<string, string> settings,
        string location,
        SourceInputSnapshot? snapshot,
        CancellationToken ct)
    {
        string alias = Setting(settings, "provider") ?? throw Configuration(
            "Choose a configured Parquet provider.", "settings.provider");
        if (!options.Providers.TryGetValue(alias, out var provider))
            throw Configuration(
                $"Unknown Parquet provider '{alias}'.",
                "settings.provider",
                $"Choose one of: {string.Join(", ", ProviderAliases)}.");

        var kind = ParseKind(provider.Kind);
        string normalized = NormalizeRelativeLocation(location);
        return kind switch
        {
            ParquetProviderKind.Local => ResolveLocal(alias, provider, normalized, snapshot),
            ParquetProviderKind.Http => await ResolveHttpAsync(alias, provider, normalized, snapshot, ct),
            ParquetProviderKind.S3 => ResolveS3(alias, provider, normalized, snapshot),
            _ => throw new InvalidOperationException(),
        };
    }

    public ParquetProviderOptions ResolveProvider(IReadOnlyDictionary<string, string> settings, out string alias)
    {
        alias = Setting(settings, "provider") ?? throw Configuration(
            "Choose a configured Parquet provider.", "settings.provider");
        if (!options.Providers.TryGetValue(alias, out var provider))
            throw Configuration($"Unknown Parquet provider '{alias}'.", "settings.provider");
        var kind = ParseKind(provider.Kind);
        switch (kind)
        {
            case ParquetProviderKind.Local:
                _ = LocalRoot(provider);
                break;
            case ParquetProviderKind.Http:
                _ = HttpBase(provider);
                break;
            case ParquetProviderKind.S3:
                string baseLocation = provider.BaseLocation.TrimEnd('/') + "/";
                if (!Uri.TryCreate(baseLocation, UriKind.Absolute, out var uri) || uri.Scheme != "s3")
                    throw Configuration("The configured S3 Parquet base must use s3://.", "settings.provider");
                break;
        }
        return provider;
    }

    public IReadOnlyList<SourceLocation> DiscoverLocal(string alias, ParquetProviderOptions provider)
    {
        if (ParseKind(provider.Kind) != ParquetProviderKind.Local)
            return [new SourceLocation(alias, PublicBase(provider), $"{provider.Kind} Parquet provider")];
        string root = LocalRoot(provider);
        if (!Directory.Exists(root)) return [];
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.EnumerateFiles(root, "*", enumeration)
            .Where(IsParquetFile)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .Select(file => new SourceLocation(alias, LogicalPath(root, file), "Parquet file"))
            .ToArray();
    }

    private static ResolvedParquetSource ResolveLocal(
        string alias,
        ParquetProviderOptions provider,
        string location,
        SourceInputSnapshot? snapshot)
    {
        string root = LocalRoot(provider);
        if (!Directory.Exists(root))
            throw Configuration(
                $"Configured Parquet root '{alias}' is unavailable.",
                "settings.provider",
                "Ask an administrator to create or mount the configured root.");

        string[] logical = snapshot is null
            ? ExpandLocal(root, location)
            : snapshot.Inputs.Select(NormalizeRelativeLocation).ToArray();
        string[] paths = logical.Select(input => SafeCombine(root, input)).ToArray();
        foreach (string path in paths)
            if (!File.Exists(path))
                throw Configuration(
                    $"Parquet input '{LogicalPath(root, path)}' no longer exists.",
                    "source.query");

        string fingerprint = Fingerprint(alias, paths.Select((path, index) =>
        {
            var file = new FileInfo(path);
            return $"{logical[index]}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }));
        VerifySnapshot(snapshot, fingerprint);
        return new ResolvedParquetSource(alias, ParquetProviderKind.Local, provider, paths, logical, fingerprint);
    }

    private async Task<ResolvedParquetSource> ResolveHttpAsync(
        string alias,
        ParquetProviderOptions provider,
        string location,
        SourceInputSnapshot? snapshot,
        CancellationToken ct)
    {
        if (HasWildcard(location))
            throw Configuration("HTTP Parquet providers require an explicit file path.", "source.query");
        Uri baseUri = HttpBase(provider);
        string[] logical = snapshot?.Inputs.Select(NormalizeRelativeLocation).ToArray() ?? [location];
        Uri[] uris = logical.Select(input => new Uri(baseUri, input)).ToArray();
        var client = httpClients.CreateClient("Colossus.Parquet");
        var evidence = new List<string>(uris.Length);
        foreach (Uri uri in uris)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var fallback = new HttpRequestMessage(HttpMethod.Get, uri);
                fallback.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var range = await client.SendAsync(fallback, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!range.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP Parquet input returned {(int)range.StatusCode} {range.ReasonPhrase}.");
                evidence.Add(HttpEvidence(logical[evidence.Count], range));
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP Parquet input returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            evidence.Add(HttpEvidence(logical[evidence.Count], response));
        }
        string fingerprint = Fingerprint(alias, evidence);
        VerifySnapshot(snapshot, fingerprint);
        return new ResolvedParquetSource(
            alias, ParquetProviderKind.Http, provider,
            uris.Select(uri => uri.AbsoluteUri).ToArray(), logical, fingerprint);
    }

    private static ResolvedParquetSource ResolveS3(
        string alias,
        ParquetProviderOptions provider,
        string location,
        SourceInputSnapshot? snapshot)
    {
        string baseLocation = provider.BaseLocation.TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseLocation, UriKind.Absolute, out var uri) || uri.Scheme != "s3")
            throw Configuration("The configured S3 Parquet base must use s3://.", "settings.provider");
        string[] logical = snapshot?.Inputs.Select(NormalizeRelativeLocation).ToArray() ?? [location];
        string[] locations = logical.Select(input => baseLocation + input).ToArray();
        string fingerprint = Fingerprint(alias, locations);
        VerifySnapshot(snapshot, fingerprint);
        return new ResolvedParquetSource(alias, ParquetProviderKind.S3, provider, locations, logical, fingerprint);
    }

    private static string[] ExpandLocal(string root, string location)
    {
        string exact = SafeCombine(root, location);
        if (File.Exists(exact))
        {
            if (!IsParquetFile(exact)) throw Configuration("Source input must be a Parquet file.", "source.query");
            return [LogicalPath(root, exact)];
        }

        string pattern = location;
        if (Directory.Exists(exact)) pattern = location.TrimEnd('/') + "/**/*.parquet";
        else if (!HasWildcard(location))
            throw Configuration($"Parquet input '{location}' does not exist.", "source.query");

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        var matcher = Glob(pattern);
        string[] files = Directory.EnumerateFiles(root, "*", enumeration)
            .Where(IsParquetFile)
            .Select(path => LogicalPath(root, path))
            .Where(path => matcher.IsMatch(path))
            .Order(StringComparer.Ordinal)
            .Take(MaximumResolvedFiles + 1)
            .ToArray();
        if (files.Length == 0)
            throw Configuration($"Parquet location '{location}' matched no files.", "source.query");
        if (files.Length > MaximumResolvedFiles)
            throw Configuration($"Parquet location '{location}' matched more than {MaximumResolvedFiles:N0} files.", "source.query");
        return files;
    }

    private static Regex Glob(string pattern)
    {
        var regex = new StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char current = pattern[i];
            if (current == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                {
                    i++;
                    regex.Append("(?:.*/)?");
                }
                else regex.Append(".*");
            }
            else if (current == '*') regex.Append("[^/]*");
            else if (current == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(current.ToString()));
        }
        regex.Append('$');
        var options = RegexOptions.CultureInvariant;
        if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
        return new Regex(regex.ToString(), options, TimeSpan.FromSeconds(1));
    }

    private static string NormalizeRelativeLocation(string value)
    {
        string location = value.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(location))
            throw Configuration("Enter a Parquet file, glob, or partitioned dataset root.", "source.query");
        if (location.IndexOf('\0') >= 0 || location.StartsWith('/') || Uri.TryCreate(location, UriKind.Absolute, out _))
            throw Configuration("Parquet locations must be relative to the configured provider.", "source.query");
        if (location.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            throw Configuration("Parquet locations cannot traverse outside the configured provider.", "source.query");
        if (location.Contains('[') || location.Contains(']') || location.Contains('{') || location.Contains('}'))
            throw Configuration("Parquet globs support only * and ? wildcards.", "source.query");
        return location.TrimStart('/');
    }

    private static string SafeCombine(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw Configuration("Parquet location left the configured provider root.", "source.query");
        return full;
    }

    private static string LocalRoot(ParquetProviderOptions provider)
    {
        ValidateBase(provider);
        return Path.GetFullPath(provider.BaseLocation);
    }

    private static Uri HttpBase(ParquetProviderOptions provider)
    {
        ValidateBase(provider);
        string location = provider.BaseLocation.EndsWith('/') ? provider.BaseLocation : provider.BaseLocation + "/";
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw Configuration("The configured HTTP Parquet base must use http:// or https://.", "settings.provider");
        return uri;
    }

    private static void ValidateBase(ParquetProviderOptions provider)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseLocation))
            throw Configuration("The configured Parquet provider has no base location.", "settings.provider");
    }

    private static ParquetProviderKind ParseKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "local" => ParquetProviderKind.Local,
        "http" or "https" => ParquetProviderKind.Http,
        "s3" => ParquetProviderKind.S3,
        _ => throw Configuration($"Unsupported Parquet provider kind '{value}'.", "settings.provider"),
    };

    private static string? Setting(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool IsParquetFile(string path) =>
        Path.GetExtension(path).Equals(".parquet", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".pq", StringComparison.OrdinalIgnoreCase);

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    private static string LogicalPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Fingerprint(string provider, IEnumerable<string> evidence)
    {
        string material = provider + "\n" + string.Join("\n", evidence);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string HttpEvidence(string logical, HttpResponseMessage response) => string.Join('|',
        logical,
        response.Headers.ETag?.Tag ?? "",
        response.Content.Headers.LastModified?.UtcTicks.ToString() ?? "",
        response.Content.Headers.ContentLength?.ToString() ?? "");

    private static void VerifySnapshot(SourceInputSnapshot? snapshot, string currentFingerprint)
    {
        if (snapshot is not null && !string.Equals(snapshot.Fingerprint, currentFingerprint, StringComparison.Ordinal))
            throw new SourceOperationException(
                SourceErrorKind.Configuration,
                "The Parquet input changed after the bake snapshot was captured.",
                new Dictionary<string, string[]> { ["source.query"] = ["Start a new bake to capture the current immutable inputs."] });
    }

    private static string PublicBase(ParquetProviderOptions provider) => ParseKind(provider.Kind) switch
    {
        ParquetProviderKind.Local => "configured local root",
        ParquetProviderKind.Http => new Uri(provider.BaseLocation).Host,
        ParquetProviderKind.S3 => provider.BaseLocation.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "bucket",
        _ => "configured provider",
    };

    private static SourceOperationException Configuration(string message, string field, string? detail = null) => new(
        SourceErrorKind.Configuration,
        message,
        new Dictionary<string, string[]> { [field] = [detail ?? message] });
}
