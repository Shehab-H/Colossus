using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Catalog;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class ProductCatalogTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-catalog-");
    private readonly List<MySqlTestDatabase> _databases = [];

    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task RepositoryContract_CreatesUpdatesListsArchivesAndEnforcesRelationships()
    {
        var (store, service) = CreateCatalog();
        var connection = await service.CreateConnectionAsync(new ConnectionDraft
        {
            Slug = "warehouse",
            Name = "Warehouse",
            Adapter = "clickhouse",
            Settings = new Dictionary<string, string> { ["url"] = "http://warehouse" },
        }, new Dictionary<string, string> { ["password"] = "top-secret" });
        var dataset = await service.CreateDatasetAsync(Dataset(connection.Id));
        var map = await service.CreateMapAsync(Map(dataset.Id, "coverage"));
        var second = await service.CreateMapAsync(Map(dataset.Id, "dominance"));

        Assert.Equal("top-secret", (await ((IConnectionRepository)store).ReadSecretsAsync(connection.Id))["password"]);
        Assert.DoesNotContain("top-secret", connection.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["coverage", "dominance"],
            (await service.ListMapsAsync()).Select(m => m.Draft.Slug));
        Assert.Equal(dataset.Id, map.Draft.DatasetId);
        Assert.Equal(dataset.Id, second.Draft.DatasetId);

        await Assert.ThrowsAsync<CatalogConflictException>(() => service.CreateMapAsync(Map(dataset.Id, "coverage")));
        await Assert.ThrowsAsync<CatalogRelationshipException>(() =>
            service.CreateDatasetAsync(Dataset(Guid.NewGuid()) with { Slug = "orphan" }));
        await Assert.ThrowsAsync<CatalogRelationshipException>(() =>
            service.CreateMapAsync(Map(Guid.NewGuid(), "orphan")));

        var updated = await service.UpdateMapAsync(
            map.Id, map.Draft with { Title = "Updated coverage" }, expectedRevision: 1);
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Updated coverage", updated.Draft.Title);
        await Assert.ThrowsAsync<CatalogConcurrencyException>(() =>
            service.UpdateMapAsync(map.Id, map.Draft, expectedRevision: 1));

        var archived = await service.ArchiveMapAsync(map.Id, expectedRevision: 2);
        Assert.True(archived.Archived);
        Assert.Equal(["dominance"], (await service.ListMapsAsync()).Select(m => m.Draft.Slug));
        Assert.Equal(2, (await service.ListMapsAsync(includeArchived: true)).Count);
    }

    [Fact]
    public async Task RestartPersistence_PreservesPolicyRunSnapshotAndRelease()
    {
        var database = CreateDatabase();
        var (firstStore, first) = CreateCatalog(database);
        var connection = await first.CreateConnectionAsync(Connection());
        var dataset = await first.CreateDatasetAsync(Dataset(connection.Id));
        var map = await first.CreateMapAsync(Map(dataset.Id, "persistent-map"));
        var policy = await first.SetBakePolicyAsync(new BakePolicy(
            map.Id, Enabled: true, Interval: TimeSpan.FromHours(6), TimeZoneId: "UTC",
            AllowOverlap: false, RetainSuccessfulReleases: 4, Revision: 0, UpdatedAt: default));
        var run = await first.CaptureBakeRunAsync(map.Id);
        _ = await first.UpdateMapAsync(map.Id, map.Draft with { Title = "New draft" }, map.Revision);
        var release = await first.RecordReleaseAsync(map.Id, run.Id, "v20260810T120000Z", "tiles/persistent-map/v1");
        var otherMap = await first.CreateMapAsync(Map(dataset.Id, "other-map"));
        var otherRun = await first.CaptureBakeRunAsync(otherMap.Id);
        await Assert.ThrowsAsync<CatalogConflictException>(() =>
            ((IReleaseRepository)firstStore).CreateAsync(new CatalogRelease(
                Guid.NewGuid(), map.Id, otherRun.Id, "invalid-cross-map", "tiles/invalid", DateTimeOffset.UtcNow)));

        var (secondStore, second) = CreateCatalog(database);
        var reloadedMap = await second.GetMapAsync(map.Id);
        var reloadedPolicy = await ((IBakePolicyRepository)secondStore).FindAsync(map.Id);
        var reloadedRun = await ((IBakeRunRepository)secondStore).FindAsync(run.Id);
        var releases = await ((IReleaseRepository)secondStore).ListForMapAsync(map.Id);

        Assert.Equal("New draft", reloadedMap.Draft.Title);
        Assert.Equal(policy, reloadedPolicy);
        Assert.Equal(map.Revision, reloadedRun!.DraftRevision);
        Assert.Equal("Map persistent-map", reloadedRun.Definition.Title);
        Assert.Equal(release, Assert.Single(releases));
        Assert.Equal("New draft", (await second.ResolveSnapshotAsync("persistent-map")).Title);
        GC.KeepAlive(firstStore);
    }

    [Fact]
    public async Task ManagementQueries_DistinguishFailedAndPublishedMaps()
    {
        var (store, catalog) = CreateCatalog();
        var connection = await catalog.CreateConnectionAsync(Connection());
        var dataset = await catalog.CreateDatasetAsync(Dataset(connection.Id));
        var failedMap = await catalog.CreateMapAsync(Map(dataset.Id, "failed-map"));
        var publishedMap = await catalog.CreateMapAsync(Map(dataset.Id, "published-map"));
        var failedSnapshot = await catalog.ResolveSnapshotAsync(failedMap.Draft.Slug);
        var publishedSnapshot = await catalog.ResolveSnapshotAsync(publishedMap.Draft.Slug);
        var now = new DateTimeOffset(2026, 8, 10, 13, 0, 0, TimeSpan.Zero);
        var failedRun = await ((IBakeRunRepository)store).CreateAsync(new BakeRun(
            Guid.NewGuid(), failedMap.Id, failedMap.Revision, failedSnapshot, BakeRunStatus.Failed,
            now, StartedAt: now, CompletedAt: now.AddMinutes(1), Error: "source unavailable"));
        var publishedRun = await ((IBakeRunRepository)store).CreateAsync(new BakeRun(
            Guid.NewGuid(), publishedMap.Id, publishedMap.Revision, publishedSnapshot, BakeRunStatus.Succeeded,
            now, StartedAt: now, CompletedAt: now.AddMinutes(2)));
        await ((IReleaseRepository)store).CreateAsync(new CatalogRelease(
            Guid.NewGuid(), publishedMap.Id, publishedRun.Id, "v20260810T130200Z",
            "tiles/published-map/v20260810T130200Z", now.AddMinutes(2)));
        var management = new MapManagementService(catalog, store, store, store, store, store);

        var maps = await management.ListAsync();
        var failed = Assert.Single(maps, map => map.Id == failedMap.Id);
        var published = Assert.Single(maps, map => map.Id == publishedMap.Id);

        Assert.Equal(MapPublicationState.LastRunFailed, failed.PublicationState);
        Assert.Equal(BakeRunStatus.Failed, failed.LastRunStatus);
        Assert.Equal(failedRun.Error, failed.LastFailure);
        Assert.Equal(MapPublicationState.Published, published.PublicationState);
        Assert.Equal("v20260810T130200Z", published.CurrentRelease);
        Assert.Equal(now.AddMinutes(2), published.PublishedAt);
    }

    [Fact]
    public async Task LegacyImport_IsIdempotentEquivalentAndNonDestructive()
    {
        string legacyDir = Path.Combine(RepoPaths.Root, "views");
        string[] files = Directory.EnumerateFiles(legacyDir, "*.json").Order(StringComparer.Ordinal).ToArray();
        var before = files.ToDictionary(file => Path.GetFileName(file)!, File.ReadAllText);
        var (store, service) = CreateCatalog();
        var importer = new LegacyViewImporter(service, Options.Create(new StorageOptions
        {
            LegacyViewsDirectory = legacyDir,
        }));

        var first = await importer.ImportAsync();
        var second = await importer.ImportAsync();

        Assert.Equal(files.Length, first.Discovered);
        Assert.Equal(files.Length, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Equal(files.Length, second.AlreadyPresent);
        Assert.Single(await service.ListConnectionsAsync());
        Assert.Equal(files.Length, (await service.ListDatasetsAsync()).Count);

        foreach (string file in files)
        {
            var legacy = ColossusJson.Deserialize<ViewConfig>(before[Path.GetFileName(file)]);
            var snapshot = await service.ResolveSnapshotAsync(legacy.Id);
            Assert.Equal(ColossusJson.Serialize(legacy), ColossusJson.Serialize(snapshot));
            Assert.Equal(before[Path.GetFileName(file)], await File.ReadAllTextAsync(file));
        }
        GC.KeepAlive(store);
    }

    [Fact]
    public void ConnectionSettings_RejectSecretLikeKeys()
    {
        var draft = Connection() with
        {
            Settings = new Dictionary<string, string> { ["apiToken"] = "must-not-be-public" },
        };
        Assert.Throws<ArgumentException>(draft.Validate);
    }

    private MySqlTestDatabase CreateDatabase()
    {
        var database = new MySqlTestDatabase();
        _databases.Add(database);
        return database;
    }

    private (MySqlDatabase Store, ProductCatalogService Service) CreateCatalog(
        MySqlTestDatabase? database = null)
    {
        database ??= CreateDatabase();
        var store = new MySqlDatabase(database.ConnectionString, new DatabaseOptions());
        var service = new ProductCatalogService(store, store, store, store, store, store,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        return (store, service);
    }

    private static ConnectionDraft Connection() => new()
    {
        Slug = "warehouse",
        Name = "Warehouse",
        Adapter = "clickhouse",
    };

    private static DatasetDraft Dataset(Guid connectionId) => new()
    {
        Slug = "coverage-source",
        Name = "Coverage source",
        ConnectionId = connectionId,
        Source = new DatasetSource
        {
            Query = "SELECT x, y, value",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            Channels =
            [
                new ChannelSpec
                {
                    Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F32,
                },
            ],
        },
    };

    private static MapDraft Map(Guid datasetId, string slug) => new()
    {
        Slug = slug,
        Title = $"Map {slug}",
        DatasetId = datasetId,
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "value" } },
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
