namespace Colossus.Infrastructure.Configuration;

public sealed class ReleaseArtifactOptions
{
    public const string Section = "ReleaseArtifacts";

    public string Provider { get; set; } = "file";
    public string? Bucket { get; set; }
    public string Prefix { get; set; } = "tiles";
    public string Region { get; set; } = "us-east-1";
    public string? ServiceUrl { get; set; }
    public bool ForcePathStyle { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    public void Normalize()
    {
        Provider = Provider.Trim().ToLowerInvariant();
        if (Provider is not ("file" or "s3"))
            throw new InvalidOperationException("ReleaseArtifacts:Provider must be 'file' or 's3'.");
        Prefix = Prefix.Trim().Trim('/');
        if (Provider == "s3" && string.IsNullOrWhiteSpace(Bucket))
            throw new InvalidOperationException("ReleaseArtifacts:Bucket is required for the S3 provider.");
        if (string.IsNullOrWhiteSpace(Region)) Region = "us-east-1";
        if ((string.IsNullOrWhiteSpace(AccessKey)) != (string.IsNullOrWhiteSpace(SecretKey)))
            throw new InvalidOperationException(
                "ReleaseArtifacts:AccessKey and SecretKey must either both be set or both be omitted.");
    }
}
