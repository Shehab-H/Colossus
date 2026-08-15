using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Abstractions.Sources;
using Colossus.Application.Catalog;

namespace Colossus.Application.Sources;

public sealed class SourcePreviewService(
    IConnectionRepository connections,
    IDatasetRepository datasets,
    ISourceAdapterCatalog adapters)
{
    public IReadOnlyList<SourceAdapterDescriptor> ListAdapters() => adapters.List();

    public async Task<SourceConnectionValidation> ValidateConnectionAsync(
        Guid connectionId, CancellationToken ct = default)
    {
        var (connection, context) = await ResolveConnectionAsync(connectionId, ct);
        return await adapters.Resolve(connection.Draft.Adapter).ValidateConnectionAsync(context, ct);
    }

    public async Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
        Guid connectionId, CancellationToken ct = default)
    {
        var (connection, context) = await ResolveConnectionAsync(connectionId, ct);
        return await adapters.Resolve(connection.Draft.Adapter).DiscoverAsync(context, ct);
    }

    public async Task<SourcePreview> PreviewDatasetAsync(
        Guid datasetId,
        SourcePreviewBudget? budget = null,
        CancellationToken ct = default)
    {
        var dataset = await datasets.FindAsync(datasetId, ct)
            ?? throw new CatalogNotFoundException($"Unknown dataset '{datasetId}'.");
        if (dataset.Archived)
            throw new CatalogRelationshipException("The dataset is archived.");

        var (connection, context) = await ResolveConnectionAsync(dataset.Draft.ConnectionId, ct);
        if (connection.WorkspaceKey != dataset.WorkspaceKey)
            throw new CatalogRelationshipException("Dataset connection belongs to another workspace.");

        var constrained = (budget ?? SourcePreviewBudget.Default).ConstrainTo(SourcePreviewBudget.Default);
        return await adapters.Resolve(connection.Draft.Adapter).PreviewAsync(
            new SourcePreviewRequest(context, dataset.Draft.Source, constrained), ct);
    }

    private async Task<(Colossus.Domain.Catalog.CatalogConnection Connection, SourceConnectionContext Context)>
        ResolveConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await connections.FindAsync(connectionId, ct)
            ?? throw new CatalogNotFoundException($"Unknown connection '{connectionId}'.");
        if (connection.Archived)
            throw new CatalogRelationshipException("The connection is archived.");
        var secrets = await connections.ReadSecretsAsync(connection.Id, ct);
        return (connection, new SourceConnectionContext(connection.Draft.Settings, secrets));
    }
}
