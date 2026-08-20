using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;

namespace Colossus.Infrastructure.Baking;

public interface IS3ObjectClient
{
    Task PutFileAsync(string key, string path, string contentType, string cacheControl);
    Task PutTextAsync(string key, string content, string contentType, string cacheControl);
    Task<byte[]?> ReadAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<IReadOnlyList<string>> ListAsync(string prefix);
    Task DeleteAsync(IReadOnlyList<string> keys);
}

/// <summary>S3-compatible release store. Immutable objects are uploaded before manifest.json; the small
/// current pointer is a separate final put, so no object-store rename semantics are assumed.</summary>
public sealed class S3ReleaseArtifactStore(
    IS3ObjectClient client,
    string bucket,
    string prefix = "tiles") : IReleaseArtifactStore
{
    private readonly string _prefix = prefix.Trim().Trim('/');

    public void MakeAddressable(ReleaseArtifactUpload release)
    {
        string manifestPath = Path.Combine(release.SourceDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"Prepared release '{release.ViewId}/{release.Version}' has no manifest.");
        string actualHash = Hash(File.ReadAllBytes(manifestPath));
        if (!string.Equals(actualHash, release.ManifestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Prepared manifest hash changed before object upload.");

        string manifestKey = Key(release.ViewId, release.Version, "manifest.json");
        if (client.ReadAsync(manifestKey).GetAwaiter().GetResult() is { } existing)
        {
            if (!string.Equals(Hash(existing), release.ManifestHash, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Object release '{release.ViewId}/{release.Version}' conflicts with this attempt.");
            return;
        }

        var files = Directory.EnumerateFiles(release.SourceDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(release.SourceDirectory, path)))
            .OrderBy(value => string.Equals(value.Relative, "manifest.json", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(value => value.Relative, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files)
        {
            string relative = file.Relative.Replace(Path.DirectorySeparatorChar, '/');
            client.PutFileAsync(
                Key([release.ViewId, release.Version, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries)]),
                file.Path,
                ContentType(relative),
                "public, max-age=31536000, immutable").GetAwaiter().GetResult();
        }
    }

    public void PublishCurrent(string viewId, string version)
    {
        if (!Exists(viewId, version))
            throw new InvalidOperationException($"Release '{viewId}/{version}' is not addressable.");
        client.PutTextAsync(
            Key(viewId, "latest.json"),
            ColossusJson.Serialize(new LatestPointer(version)),
            "application/json",
            "public, max-age=60").GetAwaiter().GetResult();
    }

    public bool TryReadCurrent(string viewId, out string version)
    {
        byte[]? bytes = client.ReadAsync(Key(viewId, "latest.json")).GetAwaiter().GetResult();
        if (bytes is null)
        {
            version = "";
            return false;
        }
        version = ColossusJson.Deserialize<LatestPointer>(System.Text.Encoding.UTF8.GetString(bytes)).Version;
        return true;
    }

    public bool Exists(string viewId, string version) =>
        client.ExistsAsync(Key(viewId, version, "manifest.json")).GetAwaiter().GetResult();

    public void Delete(string viewId, string version)
    {
        if (TryReadCurrent(viewId, out string current) && string.Equals(current, version, StringComparison.Ordinal))
            throw new InvalidOperationException($"Cannot delete current release '{viewId}/{version}'.");
        string releasePrefix = Key(viewId, version) + "/";
        var keys = client.ListAsync(releasePrefix).GetAwaiter().GetResult();
        if (keys.Count > 0) client.DeleteAsync(keys).GetAwaiter().GetResult();
    }

    public string Location(string viewId, string version) =>
        $"s3://{bucket}/{Key(viewId, version)}";

    private string Key(params string[] segments)
    {
        string suffix = string.Join('/', segments.Select(SafeSegment));
        return string.IsNullOrEmpty(_prefix) ? suffix : _prefix + "/" + suffix;
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("Object keys must consist of safe path segments.");
        return value;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => "application/json",
        ".arrow" => "application/vnd.apache.arrow.stream",
        ".parquet" => "application/vnd.apache.parquet",
        _ => "application/octet-stream",
    };
}

internal sealed class AwsS3ObjectClient : IS3ObjectClient, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public AwsS3ObjectClient(ReleaseArtifactOptions options)
    {
        _bucket = options.Bucket!;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl)) config.ServiceURL = options.ServiceUrl;
        _client = string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public async Task PutFileAsync(string key, string path, string contentType, string cacheControl)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = path,
            ContentType = contentType,
        };
        request.Headers.CacheControl = cacheControl;
        _ = await _client.PutObjectAsync(request, CancellationToken.None);
    }

    public async Task PutTextAsync(string key, string content, string contentType, string cacheControl)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = content,
            ContentType = contentType,
        };
        request.Headers.CacheControl = cacheControl;
        _ = await _client.PutObjectAsync(request, CancellationToken.None);
    }

    public async Task<byte[]?> ReadAsync(string key)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, CancellationToken.None);
            using var output = new MemoryStream();
            await response.ResponseStream.CopyToAsync(output);
            return output.ToArray();
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            _ = await _client.GetObjectMetadataAsync(_bucket, key, CancellationToken.None);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix)
    {
        var result = new List<string>();
        string? token = null;
        do
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token,
            }, CancellationToken.None);
            result.AddRange(response.S3Objects.Select(value => value.Key));
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);
        return result;
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys)
    {
        foreach (var batch in keys.Chunk(1000))
            _ = await _client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = _bucket,
                Objects = batch.Select(key => new KeyVersion { Key = key }).ToList(),
            }, CancellationToken.None);
    }

    public void Dispose() => _client.Dispose();
}
