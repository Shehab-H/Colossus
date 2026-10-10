using Colossus.Domain.Catalog;

namespace Colossus.Application.Abstractions.Catalog;

public interface IConnectionRepository
{
    Task<CatalogConnection> CreateAsync(
        CatalogConnection connection, IReadOnlyDictionary<string, string> secrets, CancellationToken ct = default);
    Task<CatalogConnection?> FindAsync(Guid id, CancellationToken ct = default);
    Task<CatalogConnection?> FindBySlugAsync(string workspaceKey, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogConnection>> ListAsync(string workspaceKey, bool includeArchived = false,
        CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> ReadSecretsAsync(Guid id, CancellationToken ct = default);
}

public interface IDatasetRepository
{
    Task<CatalogDataset> CreateAsync(CatalogDataset dataset, CancellationToken ct = default);
    Task<CatalogDataset?> FindAsync(Guid id, CancellationToken ct = default);
    Task<CatalogDataset?> FindBySlugAsync(string workspaceKey, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogDataset>> ListAsync(string workspaceKey, bool includeArchived = false,
        CancellationToken ct = default);
}

public interface IMapRepository
{
    Task<CatalogMap> CreateAsync(CatalogMap map, CancellationToken ct = default);
    Task<CatalogMap?> FindAsync(Guid id, CancellationToken ct = default);
    Task<CatalogMap?> FindBySlugAsync(string workspaceKey, string slug, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogMap>> ListAsync(string workspaceKey, bool includeArchived = false,
        CancellationToken ct = default);
    Task<CatalogMap> UpdateAsync(CatalogMap map, long expectedRevision, CancellationToken ct = default);
    Task<CatalogMap> ArchiveAsync(Guid id, long expectedRevision, DateTimeOffset updatedAt,
        CancellationToken ct = default);
}

public interface IBakePolicyRepository
{
    Task<BakePolicy?> FindAsync(Guid mapId, CancellationToken ct = default);
    Task<IReadOnlyList<BakePolicy>> ListAsync(CancellationToken ct = default);
    Task<BakePolicy> UpsertAsync(BakePolicy policy, long? expectedRevision, CancellationToken ct = default);
}

public interface IBakeRunRepository
{
    Task<BakeRun> CreateAsync(BakeRun run, CancellationToken ct = default);
    Task<BakeRun?> FindAsync(Guid id, CancellationToken ct = default);
    Task<BakeRunPage> ListPageForMapAsync(Guid mapId, BakeRunPageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<BakeRun>> ListActiveForMapAsync(Guid mapId, CancellationToken ct = default);
    Task<BakeRunOverview> GetOverviewForMapAsync(Guid mapId, CancellationToken ct = default);
    Task<IReadOnlyList<BakeRun>> ListIncompleteAsync(CancellationToken ct = default);
    Task<BakeRun> UpdateAsync(BakeRun run, CancellationToken ct = default);
    Task<BakeRun> RequestCancellationAsync(Guid id, DateTimeOffset requestedAt, CancellationToken ct = default);
    Task<BakeRun> SupersedeQueuedAsync(Guid id, long supersededByRevision, DateTimeOffset supersededAt,
        CancellationToken ct = default);
    Task<BakeRun> AttachBackgroundJobAsync(Guid id, string backgroundJobId, CancellationToken ct = default);
}

public interface IReleaseRepository
{
    Task<CatalogRelease> CreateAsync(CatalogRelease release, CancellationToken ct = default);
    Task<CatalogRelease> UpdateAsync(CatalogRelease release, CancellationToken ct = default);
    Task<CatalogRelease?> FindForRunAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogRelease>> ListForMapAsync(Guid mapId, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogRelease>> ListAllForMapAsync(Guid mapId, CancellationToken ct = default);
}
