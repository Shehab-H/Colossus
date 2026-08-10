using Colossus.Application.Abstractions.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;

namespace Colossus.Application.Catalog;

public sealed class ProductCatalogService(
    IConnectionRepository connections,
    IDatasetRepository datasets,
    IMapRepository maps,
    IBakePolicyRepository policies,
    IBakeRunRepository runs,
    IReleaseRepository releases,
    TimeProvider clock)
{
    public async Task<CatalogConnection> CreateConnectionAsync(
        ConnectionDraft draft,
        IReadOnlyDictionary<string, string>? secrets = null,
        string workspaceKey = CatalogScope.DefaultWorkspace,
        CancellationToken ct = default)
    {
        draft.Validate();
        var now = clock.GetUtcNow();
        var record = new CatalogConnection(Guid.NewGuid(), workspaceKey, draft, 1, false, now, now);
        return await connections.CreateAsync(record, secrets ?? new Dictionary<string, string>(), ct);
    }

    public Task<IReadOnlyList<CatalogConnection>> ListConnectionsAsync(
        string workspaceKey = CatalogScope.DefaultWorkspace, CancellationToken ct = default) =>
        connections.ListAsync(workspaceKey, ct: ct);

    public async Task<CatalogConnection> GetConnectionAsync(Guid id, CancellationToken ct = default) =>
        await connections.FindAsync(id, ct) ?? throw new CatalogNotFoundException($"Unknown connection '{id}'.");

    public async Task<CatalogDataset> CreateDatasetAsync(
        DatasetDraft draft,
        string workspaceKey = CatalogScope.DefaultWorkspace,
        CancellationToken ct = default)
    {
        draft.Validate();
        var connection = await connections.FindAsync(draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{draft.ConnectionId}'.");
        if (connection.Archived || connection.WorkspaceKey != workspaceKey)
            throw new CatalogRelationshipException("Dataset connection is archived or belongs to another workspace.");

        var now = clock.GetUtcNow();
        return await datasets.CreateAsync(
            new CatalogDataset(Guid.NewGuid(), workspaceKey, draft, 1, false, now, now), ct);
    }

    public Task<IReadOnlyList<CatalogDataset>> ListDatasetsAsync(
        string workspaceKey = CatalogScope.DefaultWorkspace, CancellationToken ct = default) =>
        datasets.ListAsync(workspaceKey, ct: ct);

    public async Task<CatalogDataset> GetDatasetAsync(Guid id, CancellationToken ct = default) =>
        await datasets.FindAsync(id, ct) ?? throw new CatalogNotFoundException($"Unknown dataset '{id}'.");

    public async Task<CatalogMap> CreateMapAsync(
        MapDraft draft,
        string workspaceKey = CatalogScope.DefaultWorkspace,
        CancellationToken ct = default)
    {
        await ValidateMapDraftAsync(draft, workspaceKey, ct);
        var now = clock.GetUtcNow();
        return await maps.CreateAsync(new CatalogMap(Guid.NewGuid(), workspaceKey, draft, 1, false, now, now), ct);
    }

    public Task<IReadOnlyList<CatalogMap>> ListMapsAsync(
        string workspaceKey = CatalogScope.DefaultWorkspace,
        bool includeArchived = false,
        CancellationToken ct = default) => maps.ListAsync(workspaceKey, includeArchived, ct);

    public async Task<CatalogMap> GetMapAsync(Guid id, CancellationToken ct = default) =>
        await maps.FindAsync(id, ct) ?? throw new CatalogNotFoundException($"Unknown map '{id}'.");

    public async Task<CatalogMap> GetMapBySlugAsync(
        string slug, string workspaceKey = CatalogScope.DefaultWorkspace, CancellationToken ct = default) =>
        await maps.FindBySlugAsync(workspaceKey, slug, ct)
        ?? throw new CatalogNotFoundException($"Unknown map '{slug}'.");

    public async Task<CatalogMap> UpdateMapAsync(
        Guid id,
        MapDraft draft,
        long expectedRevision,
        CancellationToken ct = default)
    {
        var current = await GetMapAsync(id, ct);
        await ValidateMapDraftAsync(draft, current.WorkspaceKey, ct);
        var updated = current with
        {
            Draft = draft,
            Revision = expectedRevision + 1,
            UpdatedAt = clock.GetUtcNow(),
        };
        return await maps.UpdateAsync(updated, expectedRevision, ct);
    }

    public async Task<CatalogMap> ArchiveMapAsync(Guid id, long expectedRevision, CancellationToken ct = default) =>
        await maps.ArchiveAsync(id, expectedRevision, clock.GetUtcNow(), ct);

    public async Task<ViewConfig> ResolveSnapshotAsync(
        string slug,
        string workspaceKey = CatalogScope.DefaultWorkspace,
        CancellationToken ct = default)
    {
        var map = await GetMapBySlugAsync(slug, workspaceKey, ct);
        if (map.Archived) throw new CatalogNotFoundException($"Map '{slug}' is archived.");
        return await ResolveSnapshotAsync(map, ct);
    }

    public async Task<BakePolicy> SetBakePolicyAsync(
        BakePolicy policy, long? expectedRevision = null, CancellationToken ct = default)
    {
        _ = await GetMapAsync(policy.MapId, ct);
        var persisted = policy with
        {
            Revision = expectedRevision is { } revision ? revision + 1 : 1,
            UpdatedAt = clock.GetUtcNow(),
        };
        persisted.Validate();
        return await policies.UpsertAsync(persisted, expectedRevision, ct);
    }

    public Task<BakePolicy?> GetBakePolicyAsync(Guid mapId, CancellationToken ct = default) =>
        policies.FindAsync(mapId, ct);

    public async Task<BakeRun> CaptureBakeRunAsync(Guid mapId, CancellationToken ct = default)
    {
        var map = await GetMapAsync(mapId, ct);
        var snapshot = await ResolveSnapshotAsync(map, ct);
        var run = new BakeRun(Guid.NewGuid(), map.Id, map.Revision, snapshot, BakeRunStatus.Queued, clock.GetUtcNow());
        return await runs.CreateAsync(run, ct);
    }

    public async Task<CatalogRelease> RecordReleaseAsync(
        Guid mapId, Guid runId, string version, string artifactLocation, CancellationToken ct = default)
    {
        _ = await GetMapAsync(mapId, ct);
        var run = await runs.FindAsync(runId, ct)
            ?? throw new CatalogRelationshipException($"Unknown bake run '{runId}'.");
        if (run.MapId != mapId) throw new CatalogRelationshipException("Bake run belongs to another map.");
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(artifactLocation))
            throw new ArgumentException("release version and artifact location are required");
        return await releases.CreateAsync(
            new CatalogRelease(Guid.NewGuid(), mapId, runId, version, artifactLocation, clock.GetUtcNow()), ct);
    }

    public async Task<CatalogMap> ImportLegacyViewAsync(
        ViewConfig view,
        string workspaceKey = CatalogScope.DefaultWorkspace,
        CancellationToken ct = default)
    {
        view.Validate();
        if (await maps.FindBySlugAsync(workspaceKey, view.Id, ct) is { } existing) return existing;

        string adapter = view.Source.Adapter.ToLowerInvariant();
        string connectionSlug = $"legacy-{adapter}";
        var connection = await connections.FindBySlugAsync(workspaceKey, connectionSlug, ct)
            ?? await CreateConnectionAsync(new ConnectionDraft
            {
                Slug = connectionSlug,
                Name = $"Imported {adapter}",
                Adapter = adapter,
            }, workspaceKey: workspaceKey, ct: ct);

        var dataset = await datasets.FindBySlugAsync(workspaceKey, view.Id, ct)
            ?? await CreateDatasetAsync(new DatasetDraft
            {
                Slug = view.Id,
                Name = view.Title ?? view.Id,
                ConnectionId = connection.Id,
                Source = DatasetSource.FromSnapshot(view.Source),
            }, workspaceKey, ct);

        return await CreateMapAsync(MapDraft.FromSnapshot(view, dataset.Id), workspaceKey, ct);
    }

    private async Task ValidateMapDraftAsync(MapDraft draft, string workspaceKey, CancellationToken ct)
    {
        var dataset = await datasets.FindAsync(draft.DatasetId, ct)
            ?? throw new CatalogRelationshipException($"Unknown dataset '{draft.DatasetId}'.");
        var connection = await connections.FindAsync(dataset.Draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{dataset.Draft.ConnectionId}'.");
        if (dataset.Archived || connection.Archived || dataset.WorkspaceKey != workspaceKey || connection.WorkspaceKey != workspaceKey)
            throw new CatalogRelationshipException("Map dependencies are archived or belong to another workspace.");
        _ = draft.ToSnapshot(dataset, connection);
    }

    private async Task<ViewConfig> ResolveSnapshotAsync(CatalogMap map, CancellationToken ct)
    {
        var dataset = await datasets.FindAsync(map.Draft.DatasetId, ct)
            ?? throw new CatalogRelationshipException($"Unknown dataset '{map.Draft.DatasetId}'.");
        var connection = await connections.FindAsync(dataset.Draft.ConnectionId, ct)
            ?? throw new CatalogRelationshipException($"Unknown connection '{dataset.Draft.ConnectionId}'.");
        return map.Draft.ToSnapshot(dataset, connection);
    }
}
