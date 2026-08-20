using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Baking;

public interface IPreparedReleaseVerifier
{
    ReleaseVerification Verify(PreparedRelease release);
}

public sealed record PreparedRelease(
    Manifest Manifest,
    string ReleaseDirectory,
    string StagingPath,
    string MarksStagingPath);

public sealed record ReleaseVerification(bool Passed, string? Message = null)
{
    public static ReleaseVerification Pass { get; } = new(true);
    public static ReleaseVerification Fail(string message) => new(false, message);
}
