using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Catalog;

namespace Colossus.Application.Catalog;

public enum MapPublicationState
{
    NeverBaked,
    Queued,
    Baking,
    Published,
    LastRunFailed,
    Archived,
}

public sealed record ManagedMapSummary(
    Guid Id,
    string Slug,
    string Title,
    string SourceType,
    long Revision,
    bool Archived,
    MapPublicationState PublicationState,
    string? CurrentRelease,
    DateTimeOffset? PublishedAt,
    BakeRunStatus? LastRunStatus,
    DateTimeOffset? LastRunAt,
    string? LastFailure,
    long? OutputBytes,
    bool ScheduleEnabled,
    DateTimeOffset? NextRunAt);

public sealed record ManagedDatasetSummary(
    Guid Id,
    string Slug,
    string Name,
    Guid ConnectionId,
    string SourceType,
    DatasetSource Source);

public sealed record ManagedBakeRun(
    Guid Id,
    long DraftRevision,
    BakeRunStatus Status,
    BakeRunStage Stage,
    double Progress,
    string? StatusMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error,
    string? FailureCategory,
    string? FailureDetails,
    long? SourceRows,
    int? TileCount,
    long? ArtifactBytes,
    Guid? ReleaseId,
    bool CancellationRequested,
    int Attempt);

public sealed record ManagedBakeRunCounts(int Queued, int Running, int Succeeded, int Failed, int Cancelled)
{
    public static ManagedBakeRunCounts From(BakeRunOverview overview) => new(
        overview.Count(BakeRunStatus.Queued),
        overview.Count(BakeRunStatus.Running),
        overview.Count(BakeRunStatus.Succeeded),
        overview.Count(BakeRunStatus.Failed),
        overview.Count(BakeRunStatus.Cancelled));
}

public sealed record ManagedBakeRunPage(IReadOnlyList<ManagedBakeRun> Runs, string? NextCursor);

public sealed record ManagedRelease(
    Guid Id,
    Guid BakeRunId,
    string Version,
    string ArtifactLocation,
    DateTimeOffset PublishedAt,
    long? OutputBytes,
    bool Pinned,
    string? ManifestHash,
    long? SourceRows,
    int? TileCount,
    bool VerificationPassed,
    DateTimeOffset? VerifiedAt);

public sealed record ManagedMapDetail(
    ManagedMapSummary Summary,
    MapDraft Definition,
    ManagedDatasetSummary Dataset,
    BakePolicy? BakePolicy,
    ManagedBakeRunCounts RunCounts,
    IReadOnlyList<ManagedRelease> Releases);

/// <summary>
/// Product-facing map queries. This is deliberately separate from the viewer compatibility lookup:
/// management pages consume durable catalog state, while an individual viewer still resolves one immutable
/// map snapshot without loading the catalog.
/// </summary>
public sealed class MapManagementService(
    ProductCatalogService catalog,
    IDatasetRepository datasets,
    IConnectionRepository connections,
    IBakePolicyRepository policies,
    IBakeRunRepository runs,
    IReleaseRepository releases,
    IReleaseArtifactStore? artifacts = null)
{
    public async Task<IReadOnlyList<ManagedMapSummary>> ListAsync(
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var maps = await catalog.ListMapsAsync(includeArchived: includeArchived, ct: ct);
        return await Task.WhenAll(maps.Select(map => SummarizeAsync(map, ct)));
    }

    public async Task<ManagedMapDetail> GetAsync(Guid id, CancellationToken ct = default)
    {
        var map = await catalog.GetMapAsync(id, ct);
        var dataset = await datasets.FindAsync(map.Draft.DatasetId, ct)
            ?? throw new CatalogRelationshipException($"Unknown dataset '{map.Draft.DatasetId}'.");
        var connection = await connections.FindAsync(dataset.Draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{dataset.Draft.ConnectionId}'.");
        var policy = await policies.FindAsync(map.Id, ct);
        var history = await runs.GetOverviewForMapAsync(map.Id, ct);
        var mapReleases = await releases.ListForMapAsync(map.Id, ct);

        return new ManagedMapDetail(
            Summarize(map, connection.Draft.Adapter, policy, history, mapReleases, CurrentVersion(map)),
            map.Draft,
            new ManagedDatasetSummary(
                dataset.Id,
                dataset.Draft.Slug,
                dataset.Draft.Name,
                dataset.Draft.ConnectionId,
                connection.Draft.Adapter,
                dataset.Draft.Source),
            policy,
            ManagedBakeRunCounts.From(history),
            mapReleases.Select(ToRelease).ToArray());
    }

    public async Task<ManagedBakeRunPage> ListRunsAsync(
        Guid mapId, BakeRunPageRequest request, CancellationToken ct = default)
    {
        _ = await catalog.GetMapAsync(mapId, ct);
        var page = await runs.ListPageForMapAsync(mapId, request.Validate(), ct);
        return new ManagedBakeRunPage(page.Runs.Select(DescribeRun).ToArray(), page.Next?.Encode());
    }

    private async Task<ManagedMapSummary> SummarizeAsync(CatalogMap map, CancellationToken ct)
    {
        var dataset = await datasets.FindAsync(map.Draft.DatasetId, ct)
            ?? throw new CatalogRelationshipException($"Unknown dataset '{map.Draft.DatasetId}'.");
        var connection = await connections.FindAsync(dataset.Draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{dataset.Draft.ConnectionId}'.");
        var policy = await policies.FindAsync(map.Id, ct);
        var history = await runs.GetOverviewForMapAsync(map.Id, ct);
        var mapReleases = await releases.ListForMapAsync(map.Id, ct);
        return Summarize(map, connection.Draft.Adapter, policy, history, mapReleases, CurrentVersion(map));
    }

    private static ManagedMapSummary Summarize(
        CatalogMap map,
        string sourceType,
        BakePolicy? policy,
        BakeRunOverview history,
        IReadOnlyList<CatalogRelease> mapReleases,
        string? currentVersion = null)
    {
        var lastRun = history.Latest;
        var currentRelease = currentVersion is null
            ? mapReleases.FirstOrDefault()
            : mapReleases.FirstOrDefault(value => value.Version == currentVersion);
        var lastFailure = history.LatestFailure;
        var publicationState = map.Archived
            ? MapPublicationState.Archived
            : lastRun?.Status switch
            {
                BakeRunStatus.Running => MapPublicationState.Baking,
                BakeRunStatus.Queued => MapPublicationState.Queued,
                BakeRunStatus.Failed => MapPublicationState.LastRunFailed,
                _ when currentRelease is not null => MapPublicationState.Published,
                _ => MapPublicationState.NeverBaked,
            };
        DateTimeOffset? nextRunAt = policy is { Enabled: true, Interval: { } interval }
            ? (lastRun?.CreatedAt ?? policy.UpdatedAt) + interval
            : null;

        return new ManagedMapSummary(
            map.Id,
            map.Draft.Slug,
            map.Draft.Title ?? map.Draft.Slug,
            sourceType,
            map.Revision,
            map.Archived,
            publicationState,
            currentRelease?.Version,
            currentRelease?.PublishedAt,
            lastRun?.Status,
            lastRun?.CompletedAt ?? lastRun?.StartedAt ?? lastRun?.CreatedAt,
            lastFailure?.Error,
            currentRelease?.ArtifactBytes,
            policy?.Enabled ?? false,
            nextRunAt);
    }

    private string? CurrentVersion(CatalogMap map) =>
        artifacts is not null && artifacts.TryReadCurrent(map.Draft.Slug, out string version)
            ? version
            : null;

    public static ManagedBakeRun DescribeRun(BakeRun run) => new(
        run.Id,
        run.DraftRevision,
        run.Status,
        run.Stage,
        run.Progress,
        run.StatusMessage,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        run.Error,
        run.FailureCategory,
        run.FailureDetails,
        run.SourceRows,
        run.TileCount,
        run.ArtifactBytes,
        run.ReleaseId,
        run.CancellationRequested,
        run.Attempt);

    private static ManagedRelease ToRelease(CatalogRelease release) => new(
        release.Id,
        release.BakeRunId,
        release.Version,
        release.ArtifactLocation,
        release.PublishedAt,
        release.ArtifactBytes,
        release.Pinned,
        release.ManifestHash,
        release.SourceRows,
        release.TileCount,
        release.VerificationPassed,
        release.VerifiedAt);
}
