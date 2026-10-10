using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Colossus.Domain.Catalog;

namespace Colossus.Application.Abstractions.Catalog;

/// <summary>Keyset position in a map's run history, ordered newest first by (created, id).</summary>
public sealed record BakeRunCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public static BakeRunCursor After(BakeRun run) => new(run.CreatedAt, run.Id);

    public string Encode() =>
        Base64Url.EncodeToString(Encoding.ASCII.GetBytes($"{CreatedAt.UtcTicks}:{Id:N}"));

    public static BakeRunCursor Decode(string token)
    {
        try
        {
            string[] parts = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(token)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                && Guid.TryParseExact(parts[1], "N", out Guid id))
                return new BakeRunCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException) { }
        catch (ArgumentOutOfRangeException) { }
        throw new ArgumentException("The run history cursor is invalid.", "cursor");
    }
}

public sealed record BakeRunPageRequest(
    int Limit,
    IReadOnlyCollection<BakeRunStatus> Statuses,
    BakeRunCursor? After = null)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public BakeRunPageRequest Validate()
    {
        if (Limit is < 1 or > MaxLimit)
            throw new ArgumentOutOfRangeException("limit", $"limit must be between 1 and {MaxLimit}");
        return this;
    }
}

public sealed record BakeRunPage(IReadOnlyList<BakeRun> Runs, BakeRunCursor? Next);

/// <summary>What summaries and the scheduler need from a map's history without loading all of it.</summary>
public sealed record BakeRunOverview(
    BakeRun? Latest,
    BakeRun? LatestFailure,
    IReadOnlyDictionary<BakeRunStatus, int> Counts)
{
    public static BakeRunOverview Empty { get; } = new(null, null, new Dictionary<BakeRunStatus, int>());

    public int Count(BakeRunStatus status) => Counts.GetValueOrDefault(status);

    public bool HasActive => Count(BakeRunStatus.Queued) + Count(BakeRunStatus.Running) > 0;
}
