using System.Net.Http.Json;
using System.Text.Json;
using Colossus.Infrastructure;
using Colossus.Infrastructure.DuckDb;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Colossus.Tests;

public sealed class ParquetSourceApiTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("colossus-parquet-api-");
    private readonly MySqlTestDatabase _database = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ParquetSourceApiTests()
    {
        string provider = Directory.CreateDirectory(Path.Combine(_directory.FullName, "provider")).FullName;
        string work = Directory.CreateDirectory(Path.Combine(_directory.FullName, "work")).FullName;
        string legacy = Directory.CreateDirectory(Path.Combine(_directory.FullName, "legacy")).FullName;
        using (var db = DuckDbSession.InMemory())
            db.Exec($"""
                COPY (SELECT * FROM (VALUES
                    (31.2::DOUBLE, 30::DOUBLE, 'cairo'::VARCHAR),
                    (31.3::DOUBLE, 30.1::DOUBLE, 'giza'::VARCHAR)
                ) v(lon, lat, label))
                TO '{Sql.Path(Path.Combine(provider, "places.parquet"))}' (FORMAT PARQUET)
                """);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Database", _database.ConnectionString)
            .UseSetting("Scheduling:Enabled", "false")
            .UseSetting("Storage:LegacyViewsDirectory", legacy)
            .UseSetting("ParquetSources:WorkDirectory", work)
            .UseSetting("ParquetSources:Providers:local:Kind", "local")
            .UseSetting("ParquetSources:Providers:local:BaseLocation", provider));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _database.Dispose();
        _directory.Delete(recursive: true);
    }

    [Fact]
    public async Task GenericSourceApi_DescribesTrinoAndOnboardsConfiguredParquetProvider()
    {
        using var client = _factory.CreateClient();
        using var adapters = JsonDocument.Parse(await client.GetStringAsync("/api/source-adapters"));
        var parquet = adapters.RootElement.EnumerateArray().Single(adapter => adapter.GetProperty("id").GetString() == "parquet");
        Assert.Equal("Parquet location", parquet.GetProperty("datasetInputLabel").GetString());
        var providerField = Assert.Single(parquet.GetProperty("configurationFields").EnumerateArray());
        Assert.Equal("local", Assert.Single(providerField.GetProperty("allowedValues").EnumerateArray()).GetString());
        var trino = adapters.RootElement.EnumerateArray().Single(adapter => adapter.GetProperty("id").GetString() == "trino");
        Assert.Equal("Trino SQL", trino.GetProperty("datasetInputLabel").GetString());
        Assert.Contains(
            trino.GetProperty("configurationFields").EnumerateArray(),
            field => field.GetProperty("key").GetString() == "sessionProperties");

        var connectionResponse = await client.PostAsJsonAsync("/api/connections", new
        {
            definition = new
            {
                slug = "local-parquet",
                name = "Local Parquet",
                adapter = "parquet",
                settings = new { provider = "local" },
            },
        });
        connectionResponse.EnsureSuccessStatusCode();
        using var connection = JsonDocument.Parse(await connectionResponse.Content.ReadAsStringAsync());
        Guid connectionId = connection.RootElement.GetProperty("id").GetGuid();

        (await client.PostAsync($"/api/connections/{connectionId}/test", null)).EnsureSuccessStatusCode();
        using var locations = JsonDocument.Parse(await client.GetStringAsync($"/api/connections/{connectionId}/locations"));
        Assert.Equal("places.parquet", Assert.Single(locations.RootElement.EnumerateArray()).GetProperty("name").GetString());

        var schemaResponse = await client.PostAsJsonAsync($"/api/connections/{connectionId}/schema", new { query = "places.parquet" });
        schemaResponse.EnsureSuccessStatusCode();
        using var schema = JsonDocument.Parse(await schemaResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            ["lon", "lat", "label"],
            schema.RootElement.GetProperty("columns").EnumerateArray().Select(column => column.GetProperty("name").GetString()));

        var datasetResponse = await client.PostAsJsonAsync("/api/datasets", new
        {
            slug = "parquet-places",
            name = "Parquet places",
            connectionId,
            source = new
            {
                query = "places.parquet",
                geometry = new { kind = "lonLat", lon = "lon", lat = "lat" },
                channels = new[] { new { name = "label", column = "label", role = "dimension", type = "dict" } },
            },
        });
        datasetResponse.EnsureSuccessStatusCode();
        using var dataset = JsonDocument.Parse(await datasetResponse.Content.ReadAsStringAsync());
        Guid datasetId = dataset.RootElement.GetProperty("id").GetGuid();

        var previewResponse = await client.PostAsync($"/api/datasets/{datasetId}/preview", null);
        previewResponse.EnsureSuccessStatusCode();
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, preview.RootElement.GetProperty("geometry").GetProperty("sampledRows").GetInt32());
        Assert.Equal("cairo", preview.RootElement.GetProperty("rows")[0].GetProperty("label").GetString());
    }
}
