using Colossus.Application.Abstractions.Sources;
using Colossus.Application.Catalog;
using Colossus.Application.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Catalog;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Sources;
using Xunit;

namespace Colossus.Tests;

public sealed class SourcePreviewServiceTests : IDisposable
{
    private readonly MySqlTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Preview_ResolvesSecretsAndConstrainsCallerBudgets()
    {
        var (store, catalog) = Catalog();
        var connection = await catalog.CreateConnectionAsync(new ConnectionDraft
        {
            Slug = "preview-warehouse",
            Name = "Preview warehouse",
            Adapter = "deterministic",
            Settings = new Dictionary<string, string> { ["url"] = "https://source.example" },
        }, new Dictionary<string, string> { ["password"] = "server-only" });
        var dataset = await catalog.CreateDatasetAsync(Dataset(connection.Id));
        var adapter = new DeterministicAdapter();
        var service = new SourcePreviewService(store, store, new SourceAdapterCatalog([adapter]));

        var result = await service.PreviewDatasetAsync(dataset.Id, new SourcePreviewBudget(
            MaxRows: 10_000,
            MaxResultBytes: 100 * 1024 * 1024,
            MaxScannedRows: 100_000_000,
            MaxDuration: TimeSpan.FromMinutes(5)));

        Assert.Single(result.Rows);
        Assert.Equal(SourcePreviewBudget.Default, adapter.Request!.Budget);
        Assert.Equal("https://source.example", adapter.Request.Connection.Settings["url"]);
        Assert.Equal("server-only", adapter.Request.Connection.Secrets["password"]);
        Assert.Equal("deterministic", Assert.Single(service.ListAdapters()).Id);
    }

    [Fact]
    public async Task Cancellation_ReachesTheUnderlyingAdapter()
    {
        var (store, catalog) = Catalog();
        var connection = await catalog.CreateConnectionAsync(new ConnectionDraft
        {
            Slug = "cancel-source",
            Name = "Cancel source",
            Adapter = "deterministic",
        });
        var dataset = await catalog.CreateDatasetAsync(Dataset(connection.Id) with { Slug = "cancel-data" });
        var adapter = new DeterministicAdapter { BlockUntilCancelled = true };
        var service = new SourcePreviewService(store, store, new SourceAdapterCatalog([adapter]));
        using var cancellation = new CancellationTokenSource();

        var pending = service.PreviewDatasetAsync(dataset.Id, ct: cancellation.Token);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(adapter.CancellationObserved);
    }

    [Fact]
    public async Task StructuredAdapterErrors_RemainFieldAddressable()
    {
        var (store, catalog) = Catalog();
        var connection = await catalog.CreateConnectionAsync(new ConnectionDraft
        {
            Slug = "invalid-source",
            Name = "Invalid source",
            Adapter = "deterministic",
        });
        var dataset = await catalog.CreateDatasetAsync(Dataset(connection.Id) with { Slug = "invalid-data" });
        var adapter = new DeterministicAdapter
        {
            Failure = new SourceOperationException(
                SourceErrorKind.Type,
                "Coordinates are invalid.",
                new Dictionary<string, string[]> { ["source.geometry"] = ["Latitude exceeds 90 degrees."] }),
        };
        var service = new SourcePreviewService(store, store, new SourceAdapterCatalog([adapter]));

        var error = await Assert.ThrowsAsync<SourceOperationException>(() => service.PreviewDatasetAsync(dataset.Id));

        Assert.Equal(SourceErrorKind.Type, error.Kind);
        Assert.Equal("Latitude exceeds 90 degrees.", error.FieldErrors["source.geometry"].Single());
    }

    private (MySqlDatabase Store, ProductCatalogService Catalog) Catalog()
    {
        var store = new MySqlDatabase(_database.ConnectionString, new DatabaseOptions());
        var catalog = new ProductCatalogService(store, store, store, store, store, store, TimeProvider.System);
        return (store, catalog);
    }

    private static DatasetDraft Dataset(Guid connectionId) => new()
    {
        Slug = "preview-data",
        Name = "Preview data",
        ConnectionId = connectionId,
        Source = new DatasetSource
        {
            Query = "SELECT longitude, latitude, value FROM fixture",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "longitude", Lat = "latitude" },
            Channels =
            [
                new ChannelSpec
                {
                    Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64,
                },
            ],
        },
    };

    private sealed class DeterministicAdapter : ISourceAdapter
    {
        public SourceAdapterDescriptor Descriptor { get; } = new(
            "deterministic",
            "Deterministic",
            new(true, true, true, true, true),
            [new SourceConfigurationField("url", "URL", true)],
            [GeometryKind.LonLat]);

        public SourcePreviewRequest? Request { get; private set; }
        public bool BlockUntilCancelled { get; init; }
        public bool CancellationObserved { get; private set; }
        public SourceOperationException? Failure { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SourceConnectionValidation> ValidateConnectionAsync(
            SourceConnectionContext connection, CancellationToken ct = default) =>
            Task.FromResult(new SourceConnectionValidation("Ready", new Dictionary<string, string>()));

        public Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
            SourceConnectionContext connection, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SourceLocation>>([new("fixture", "points", "table")]);

        public async Task<SourcePreview> PreviewAsync(
            SourcePreviewRequest request, CancellationToken ct = default)
        {
            Request = request;
            Entered.TrySetResult();
            if (Failure is not null) throw Failure;
            if (BlockUntilCancelled)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            return new SourcePreview(
                [new("x", "Float64", false, "geometry"), new("y", "Float64", false, "geometry")],
                [new Dictionary<string, string?> { ["x"] = "31.2", ["y"] = "30.0" }],
                new SourceGeometryPreview(GeometryKind.LonLat, true, new Bbox(31.2, 30, 31.2, 30), 1, 1),
                Truncated: false,
                ResultBytes: 32,
                Elapsed: TimeSpan.FromMilliseconds(1));
        }

        public Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default) =>
            Task.FromResult(new SourceBounds(new Bbox(31.2, 30, 31.2, 30), 1, 1));

        public Task ExtractAsync(
            ViewConfig view,
            Bbox bounds,
            string destinationParquet,
            IProgress<SourceExtractProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
