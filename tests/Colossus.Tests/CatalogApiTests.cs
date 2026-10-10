using System.Net.Http.Json;
using System.Text.Json;
using Colossus.Application.Abstractions.Sources;
using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Colossus.Tests;

public sealed class CatalogApiTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-api-");
    private readonly MySqlTestDatabase _database = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ApiSourceAdapter _sourceAdapter = new();

    public CatalogApiTests()
    {
        string legacy = Path.Combine(_dir.FullName, "legacy");
        Directory.CreateDirectory(legacy);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Database", _database.ConnectionString)
            .UseSetting("Scheduling:Enabled", "false")
            .UseSetting("Storage:LegacyViewsDirectory", legacy)
            .UseSetting("Storage:TilesDirectory", Path.Combine(_dir.FullName, "tiles"))
            .UseSetting("Storage:StagingDirectory", Path.Combine(_dir.FullName, "staging"))
            .ConfigureServices(services =>
            {
                services.RemoveAll<ISourceAdapter>();
                services.RemoveAll<ISourceAdapterCatalog>();
                services.AddSingleton<ISourceAdapter>(_sourceAdapter);
                services.AddSingleton<ISourceAdapterCatalog, SourceAdapterCatalog>();
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _database.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task ConnectionResponses_NeverSerializeSecrets()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/connections", new
        {
            definition = new
            {
                slug = "private-warehouse",
                name = "Private warehouse",
                adapter = "clickhouse",
                settings = new { url = "https://warehouse.example" },
            },
            secrets = new { password = "should-never-leave-the-server", apiKey = "also-private" },
        });
        response.EnsureSuccessStatusCode();
        string created = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(created);
        string id = document.RootElement.GetProperty("id").GetString()!;

        AssertSecretFree(created);
        AssertSecretFree(await client.GetStringAsync($"/api/connections/{id}"));
        AssertSecretFree(await client.GetStringAsync("/api/connections"));
    }

    [Fact]
    public async Task LegacyViewUrl_ResolvesThroughTheCatalogLookup()
    {
        string legacy = Path.Combine(_dir.FullName, "legacy");
        File.Copy(Path.Combine(Colossus.Infrastructure.RepoPaths.Root, "views", "geonames.json"),
            Path.Combine(legacy, "geonames.json"));
        using var client = _factory.CreateClient();

        using var view = JsonDocument.Parse(await client.GetStringAsync("/api/views/geonames"));
        using var url = JsonDocument.Parse(await client.GetStringAsync("/api/views/geonames/url"));

        Assert.Equal("geonames", view.RootElement.GetProperty("id").GetString());
        Assert.Equal("geonames", url.RootElement.GetProperty("id").GetString());
        Assert.EndsWith("/maps/geonames", url.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public async Task MapManagementFlow_ReportsDurableStateAndArchivesWithoutDeleting()
    {
        using var client = _factory.CreateClient();
        var datasetId = await CreateDatasetAsync(client);
        using var created = await PostMapAsync(client, datasetId, "managed-map");
        string mapId = created.RootElement.GetProperty("id").GetString()!;

        using (var list = JsonDocument.Parse(await client.GetStringAsync("/api/maps")))
        {
            var summary = Assert.Single(list.RootElement.EnumerateArray());
            Assert.Equal("neverBaked", summary.GetProperty("publicationState").GetString());
            Assert.Equal("clickhouse", summary.GetProperty("sourceType").GetString());
            Assert.False(summary.GetProperty("scheduleEnabled").GetBoolean());
        }

        var policy = await client.PutAsJsonAsync($"/api/maps/{mapId}/bake-policy", new
        {
            enabled = true,
            interval = "1.00:00:00",
            timeZoneId = "UTC",
            allowOverlap = false,
            retainSuccessfulReleases = 3,
        });
        policy.EnsureSuccessStatusCode();

        var queued = await client.PostAsync($"/api/maps/{mapId}/runs", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, queued.StatusCode);
        using var queuedJson = JsonDocument.Parse(await queued.Content.ReadAsStringAsync());
        Guid runId = queuedJson.RootElement.GetProperty("id").GetGuid();

        using (var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/maps/{mapId}")))
        {
            Assert.Equal("queued", detail.RootElement.GetProperty("summary").GetProperty("publicationState").GetString());
            Assert.True(detail.RootElement.GetProperty("bakePolicy").GetProperty("enabled").GetBoolean());
            Assert.Equal(1, detail.RootElement.GetProperty("runCounts").GetProperty("queued").GetInt32());
            Assert.Equal(
                "#d55e00",
                detail.RootElement.GetProperty("definition").GetProperty("encoding").GetProperty("color")
                    .GetProperty("overrides").GetProperty("priority").GetString());
        }

        using (var runs = JsonDocument.Parse(await client.GetStringAsync($"/api/maps/{mapId}/runs?status=queued&limit=5")))
        {
            var run = Assert.Single(runs.RootElement.GetProperty("runs").EnumerateArray());
            Assert.Equal(runId, run.GetProperty("id").GetGuid());
            Assert.Equal("queued", run.GetProperty("status").GetString());
            Assert.False(runs.RootElement.TryGetProperty("nextCursor", out _));
        }
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/maps/{mapId}/runs?cursor=not-a-cursor")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/maps/{mapId}/runs?limit=0")).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<IBakeRunRepository>().FindAsync(runId);
            Assert.Equal("#d55e00", run!.Definition.Encoding!.Color!.Overrides!["priority"]);
        }

        var cancel = await client.PostAsync($"/api/maps/{mapId}/runs/{runId}/cancel", content: null);
        cancel.EnsureSuccessStatusCode();
        using (var cancelled = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync()))
        {
            Assert.Equal("cancelled", cancelled.RootElement.GetProperty("status").GetString());
            Assert.True(cancelled.RootElement.GetProperty("cancellationRequested").GetBoolean());
        }

        var archived = await client.DeleteAsync($"/api/maps/{mapId}?expectedRevision=1");
        archived.EnsureSuccessStatusCode();
        using var archivedList = JsonDocument.Parse(await client.GetStringAsync("/api/maps?includeArchived=true"));
        var archivedSummary = Assert.Single(archivedList.RootElement.EnumerateArray());
        Assert.Equal("archived", archivedSummary.GetProperty("publicationState").GetString());
        Assert.True(archivedSummary.GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task ReleasePinAndReconciliation_KeepCurrentAndDeleteOnlyExpiredVersions()
    {
        using var client = _factory.CreateClient();
        Guid datasetId = await CreateDatasetAsync(client);
        using var created = await PostMapAsync(client, datasetId, "release-map");
        Guid mapId = created.RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<ProductCatalogService>();
        var runRepository = scope.ServiceProvider.GetRequiredService<IBakeRunRepository>();
        var releaseRepository = scope.ServiceProvider.GetRequiredService<IReleaseRepository>();
        var work = scope.ServiceProvider.GetRequiredService<IBakeStore>();
        var artifacts = scope.ServiceProvider.GetRequiredService<IReleaseArtifactStore>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        async Task<CatalogRelease> AddRelease(string version, DateTimeOffset published, bool current)
        {
            BakeRun run = await catalog.CaptureBakeRunAsync(mapId);
            _ = await runRepository.UpdateAsync(run with
            {
                Status = BakeRunStatus.Succeeded,
                Stage = BakeRunStage.Succeeded,
                CompletedAt = published,
            });
            string attempt = $"{run.Id:N}-a1";
            var manifest = ApiManifest("release-map", version);
            await work.WriteManifestAsync(manifest, attempt);
            string directory = work.VersionDirectory("release-map", version, attempt);
            string hash = work.PreparedManifestHash("release-map", version, attempt);
            long bytes = work.PreparedVersionSize("release-map", version, attempt);
            artifacts.MakeAddressable(new ReleaseArtifactUpload(
                "release-map", version, attempt, directory, hash));
            if (current) artifacts.PublishCurrent("release-map", version);
            return await releaseRepository.CreateAsync(new CatalogRelease(
                Guid.NewGuid(), mapId, run.Id, version, artifacts.Location("release-map", version),
                published, bytes,
                ManifestHash: hash, SourceSnapshot: run.Definition, SourceRows: 1, TileCount: 1,
                VerificationPassed: true, VerifiedAt: published, PreparedAt: published, AttemptId: attempt));
        }

        CatalogRelease expired = await AddRelease("v-expired", now.AddDays(-30), current: false);
        _ = await AddRelease("v-current", now, current: true);

        var policy = await client.PutAsJsonAsync($"/api/maps/{mapId}/bake-policy", new
        {
            enabled = false,
            timeZoneId = "UTC",
            allowOverlap = false,
            retainSuccessfulReleases = 1,
            retainSuccessfulFor = "01:00:00",
        });
        policy.EnsureSuccessStatusCode();

        var pin = await client.PutAsync($"/api/maps/{mapId}/releases/{expired.Id}/pin?pinned=true", null);
        pin.EnsureSuccessStatusCode();
        using (var pinned = JsonDocument.Parse(await pin.Content.ReadAsStringAsync()))
            Assert.True(pinned.RootElement.GetProperty("pinned").GetBoolean());
        var unpin = await client.PutAsync($"/api/maps/{mapId}/releases/{expired.Id}/pin?pinned=false", null);
        unpin.EnsureSuccessStatusCode();

        var reconcile = await client.PostAsync($"/api/maps/{mapId}/releases/reconcile", null);
        reconcile.EnsureSuccessStatusCode();
        using var report = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.Contains(report.RootElement.GetProperty("decisions").EnumerateArray(), value =>
            value.GetProperty("identity").GetString() == "v-expired" &&
            value.GetProperty("action").GetString() == "deleted");
        Assert.Contains(report.RootElement.GetProperty("decisions").EnumerateArray(), value =>
            value.GetProperty("identity").GetString() == "v-current" &&
            value.GetProperty("reason").GetString() == "current");
        Assert.True(artifacts.TryReadCurrent("release-map", out string currentVersion));
        Assert.Equal("v-current", currentVersion);
        Assert.False(artifacts.Exists("release-map", "v-expired"));
    }

    [Fact]
    public async Task MapValidation_ReturnsFieldErrorsForTheEditor()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/maps", new
        {
            schemaVersion = 1,
            slug = "Not Valid",
            title = "Invalid",
            datasetId = Guid.Empty,
            viewport = "geo",
            mark = "point",
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("slug", out _));
        Assert.True(errors.TryGetProperty("datasetId", out _));
    }

    [Fact]
    public async Task SourcePreviewEndpoints_ExposeCapabilitiesAndStructuredResultsWithoutSecrets()
    {
        using var client = _factory.CreateClient();
        using var adapters = JsonDocument.Parse(await client.GetStringAsync("/api/source-adapters"));
        var descriptor = Assert.Single(adapters.RootElement.EnumerateArray());
        Assert.Equal("fixture", descriptor.GetProperty("id").GetString());
        Assert.True(descriptor.GetProperty("capabilities").GetProperty("boundedPreview").GetBoolean());

        var connectionResponse = await client.PostAsJsonAsync("/api/connections", new
        {
            definition = new
            {
                slug = "preview-fixture",
                name = "Preview fixture",
                adapter = "fixture",
                settings = new { url = "https://fixture.example" },
            },
            secrets = new { password = "never-return-this" },
        });
        connectionResponse.EnsureSuccessStatusCode();
        using var connection = JsonDocument.Parse(await connectionResponse.Content.ReadAsStringAsync());
        Guid connectionId = connection.RootElement.GetProperty("id").GetGuid();

        var testResponse = await client.PostAsync($"/api/connections/{connectionId}/test", null);
        testResponse.EnsureSuccessStatusCode();
        Assert.Equal("never-return-this", _sourceAdapter.LastConnection!.Secrets["password"]);
        using var locations = JsonDocument.Parse(await client.GetStringAsync($"/api/connections/{connectionId}/locations"));
        Assert.Equal("points", Assert.Single(locations.RootElement.EnumerateArray()).GetProperty("name").GetString());

        var datasetResponse = await client.PostAsJsonAsync("/api/datasets", new
        {
            slug = "preview-fixture-data",
            name = "Preview fixture data",
            connectionId,
            source = new
            {
                query = "SELECT lon, lat, value FROM points",
                geometry = new { kind = "lonLat", lon = "lon", lat = "lat" },
                channels = new[] { new { name = "value", column = "value", role = "measure", type = "f64" } },
            },
        });
        datasetResponse.EnsureSuccessStatusCode();
        using var dataset = JsonDocument.Parse(await datasetResponse.Content.ReadAsStringAsync());
        Guid datasetId = dataset.RootElement.GetProperty("id").GetGuid();

        var previewResponse = await client.PostAsync($"/api/datasets/{datasetId}/preview", null);
        previewResponse.EnsureSuccessStatusCode();
        string previewJson = await previewResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("never-return-this", previewJson);
        using var preview = JsonDocument.Parse(previewJson);
        Assert.Equal(2, preview.RootElement.GetProperty("geometry").GetProperty("sampledRows").GetInt32());
        Assert.Equal("31.2", preview.RootElement.GetProperty("rows")[0].GetProperty("x").GetString());

        _sourceAdapter.PreviewError = new SourceOperationException(
            SourceErrorKind.Type,
            "The geometry mapping is invalid.",
            new Dictionary<string, string[]> { ["source.geometry"] = ["Longitude and latitude columns appear swapped."] });
        var invalidPreview = await client.PostAsync($"/api/datasets/{datasetId}/preview", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalidPreview.StatusCode);
        using var problem = JsonDocument.Parse(await invalidPreview.Content.ReadAsStringAsync());
        Assert.Equal("Type", problem.RootElement.GetProperty("sourceErrorKind").GetString());
        Assert.Equal(
            "Longitude and latitude columns appear swapped.",
            problem.RootElement.GetProperty("errors").GetProperty("source.geometry")[0].GetString());
    }

    private static async Task<Guid> CreateDatasetAsync(HttpClient client)
    {
        var connection = await client.PostAsJsonAsync("/api/connections", new
        {
            definition = new { slug = "management-source", name = "Management source", adapter = "clickhouse" },
        });
        connection.EnsureSuccessStatusCode();
        using var connectionJson = JsonDocument.Parse(await connection.Content.ReadAsStringAsync());
        var connectionId = connectionJson.RootElement.GetProperty("id").GetGuid();

        var dataset = await client.PostAsJsonAsync("/api/datasets", new
        {
            slug = "management-dataset",
            name = "Management dataset",
            connectionId,
            source = new
            {
                query = "SELECT lon, lat, value FROM points",
                geometry = new { kind = "lonLat", lon = "lon", lat = "lat" },
                channels = new[] { new { name = "value", column = "value", role = "measure", type = "f64" } },
            },
        });
        dataset.EnsureSuccessStatusCode();
        using var datasetJson = JsonDocument.Parse(await dataset.Content.ReadAsStringAsync());
        return datasetJson.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> PostMapAsync(HttpClient client, Guid datasetId, string slug)
    {
        var response = await client.PostAsJsonAsync("/api/maps", new
        {
            schemaVersion = 1,
            slug,
            title = "Managed map",
            datasetId,
            viewport = "geo",
            mark = "point",
            encoding = new
            {
                color = new
                {
                    channel = "value",
                    type = "categorical",
                    scheme = "okabeIto",
                    domain = new[] { "standard", "priority" },
                    overrides = new { priority = "#d55e00" },
                    unknown = "#999999",
                },
            },
            inspect = new { channels = new[] { "value" } },
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Manifest ApiManifest(string viewId, string version) => new()
    {
        Version = version,
        View = new ViewConfig
        {
            Id = viewId,
            Viewport = Viewport.Geo,
            Mark = Mark.Point,
            Source = new SourceSpec
            {
                Query = "SELECT x, y FROM fixture",
                Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            },
        },
        Reduction = ReductionKind.RawPassthrough,
        Regime = "large",
        Root = new Bbox(0, 0, 1, 1),
        MinZoom = 0,
        MaxZoom = 0,
        TilePointBudget = 10,
        TotalPoints = 1,
        SourceRows = 1,
        Tiles = [new TileMeta(0, 0, 0, 1, true)],
    };

    private static void AssertSecretFree(string json)
    {
        Assert.DoesNotContain("should-never-leave-the-server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("also-private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secrets", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ApiSourceAdapter : ISourceAdapter
    {
        public SourceAdapterDescriptor Descriptor { get; } = new(
            "fixture",
            "Fixture source",
            new(true, true, true, true, true),
            [new SourceConfigurationField("url", "URL", true), new SourceConfigurationField("password", "Password", false, true)],
            [GeometryKind.LonLat]);

        public SourceConnectionContext? LastConnection { get; private set; }
        public SourceOperationException? PreviewError { get; set; }

        public Task<SourceConnectionValidation> ValidateConnectionAsync(
            SourceConnectionContext connection, CancellationToken ct = default)
        {
            LastConnection = connection;
            return Task.FromResult(new SourceConnectionValidation(
                "Connection succeeded.", new Dictionary<string, string> { ["serverVersion"] = "fixture-1" }));
        }

        public Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
            SourceConnectionContext connection, CancellationToken ct = default)
        {
            LastConnection = connection;
            return Task.FromResult<IReadOnlyList<SourceLocation>>([new("fixture", "points", "table")]);
        }

        public Task<SourcePreview> PreviewAsync(SourcePreviewRequest request, CancellationToken ct = default)
        {
            LastConnection = request.Connection;
            if (PreviewError is not null) throw PreviewError;
            return Task.FromResult(new SourcePreview(
                [new("x", "Float64", false, "geometry"), new("y", "Float64", false, "geometry"), new("value", "Float64", false, "measure")],
                [
                    new Dictionary<string, string?> { ["x"] = "31.2", ["y"] = "30", ["value"] = "12" },
                    new Dictionary<string, string?> { ["x"] = "31.3", ["y"] = "30.1", ["value"] = "14" },
                ],
                new SourceGeometryPreview(GeometryKind.LonLat, true, new Bbox(31.2, 30, 31.3, 30.1), 2, 2),
                false, 128, TimeSpan.FromMilliseconds(2)));
        }

        public Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default) =>
            Task.FromResult(new SourceBounds(new Bbox(31.2, 30, 31.3, 30.1), 2, 2));

        public Task ExtractAsync(
            ViewConfig view,
            Bbox bounds,
            string destinationParquet,
            IProgress<SourceExtractProgress>? progress = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
