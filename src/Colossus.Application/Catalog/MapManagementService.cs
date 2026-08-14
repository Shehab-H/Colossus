using Colossus.Application.Abstractions.Catalog;
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
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

public sealed record ManagedRelease(
    Guid Id,
    Guid BakeRunId,
    string Version,
    string ArtifactLocation,
    DateTimeOffset PublishedAt,
    long? OutputBytes);

public sealed record ManagedMapDetail(
    ManagedMapSummary Summary,
    MapDraft Definition,
    ManagedDatasetSummary Dataset,
    BakePolicy? BakePolicy,
    IReadOnlyList<ManagedBakeRun> Runs,
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
    IReleaseRepository releases)
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
        var mapRuns = await runs.ListForMapAsync(map.Id, ct);
        var mapReleases = await releases.ListForMapAsync(map.Id, ct);

        return new ManagedMapDetail(
            Summarize(map, connection.Draft.Adapter, policy, mapRuns, mapReleases),
            map.Draft,
            new ManagedDatasetSummary(
                dataset.Id,
                dataset.Draft.Slug,
                dataset.Draft.Name,
                dataset.Draft.ConnectionId,
                connection.Draft.Adapter,
                dataset.Draft.Source),
            policy,
            mapRuns.Select(ToRun).ToArray(),
            mapReleases.Select(ToRelease).ToArray());
    }

    public async Task<ManagedBakeRun> RunNowAsync(Guid mapId, CancellationToken ct = default) =>
        ToRun(await catalog.CaptureBakeRunAsync(mapId, ct));

    private async Task<ManagedMapSummary> SummarizeAsync(CatalogMap map, CancellationToken ct)
    {
        var dataset = await datasets.FindAsync(map.Draft.DatasetId, ct)
            ?? throw new CatalogRelationshipException($"Unknown dataset '{map.Draft.DatasetId}'.");
        var connection = await connections.FindAsync(dataset.Draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{dataset.Draft.ConnectionId}'.");
        var policy = await policies.FindAsync(map.Id, ct);
        var mapRuns = await runs.ListForMapAsync(map.Id, ct);
        var mapReleases = await releases.ListForMapAsync(map.Id, ct);
        return Summarize(map, connection.Draft.Adapter, policy, mapRuns, mapReleases);
    }

    private static ManagedMapSummary Summarize(
        CatalogMap map,
        string sourceType,
        BakePolicy? policy,
        IReadOnlyList<BakeRun> mapRuns,
        IReadOnlyList<CatalogRelease> mapReleases)
    {
        var lastRun = mapRuns.FirstOrDefault();
        var currentRelease = mapReleases.FirstOrDefault();
        var lastFailure = mapRuns.FirstOrDefault(run => run.Status == BakeRunStatus.Failed);
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
            ? policy.UpdatedAt + interval
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
            OutputBytes: null,
            policy?.Enabled ?? false,
            nextRunAt);
    }

    private static ManagedBakeRun ToRun(BakeRun run) => new(
        run.Id,
        run.DraftRevision,
        run.Status,
        run.CreatedAt,
        run.StartedAt,
        run.CompletedAt,
        run.Error);

    private static ManagedRelease ToRelease(CatalogRelease release) => new(
        release.Id,
        release.BakeRunId,
        release.Version,
        release.ArtifactLocation,
        release.PublishedAt,
        OutputBytes: null);
}
