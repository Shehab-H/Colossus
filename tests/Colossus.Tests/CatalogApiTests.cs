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
