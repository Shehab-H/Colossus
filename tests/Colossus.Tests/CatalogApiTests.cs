using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Colossus.Tests;

public sealed class CatalogApiTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-api-");
    private readonly MySqlTestDatabase _database = new();
    private readonly WebApplicationFactory<Program> _factory;

    public CatalogApiTests()
    {
        string legacy = Path.Combine(_dir.FullName, "legacy");
        Directory.CreateDirectory(legacy);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Database", _database.ConnectionString)
            .UseSetting("Storage:LegacyViewsDirectory", legacy));
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

        using (var detail = JsonDocument.Parse(await client.GetStringAsync($"/api/maps/{mapId}")))
        {
            Assert.Equal("queued", detail.RootElement.GetProperty("summary").GetProperty("publicationState").GetString());
            Assert.True(detail.RootElement.GetProperty("bakePolicy").GetProperty("enabled").GetBoolean());
            Assert.Equal("queued", Assert.Single(detail.RootElement.GetProperty("runs").EnumerateArray()).GetProperty("status").GetString());
        }

        var archived = await client.DeleteAsync($"/api/maps/{mapId}?expectedRevision=1");
        archived.EnsureSuccessStatusCode();
        using var archivedList = JsonDocument.Parse(await client.GetStringAsync("/api/maps?includeArchived=true"));
        var archivedSummary = Assert.Single(archivedList.RootElement.EnumerateArray());
        Assert.Equal("archived", archivedSummary.GetProperty("publicationState").GetString());
        Assert.True(archivedSummary.GetProperty("archived").GetBoolean());
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
            encoding = new { color = new { channel = "value" } },
            inspect = new { channels = new[] { "value" } },
        });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static void AssertSecretFree(string json)
    {
        Assert.DoesNotContain("should-never-leave-the-server", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("also-private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secrets", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings", json, StringComparison.OrdinalIgnoreCase);
    }
}
