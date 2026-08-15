using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;
using Xunit;

namespace Colossus.Tests;

public sealed class HangfireMySqlStorageTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-hangfire-mysql-");
    private readonly MySqlTestDatabase _database = new();
    private readonly WebApplicationFactory<Program> _factory;

    public HangfireMySqlStorageTests()
    {
        string legacy = Path.Combine(_dir.FullName, "legacy");
        Directory.CreateDirectory(legacy);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Database", _database.ConnectionString)
            .UseSetting("Scheduling:Enabled", "true")
            .UseSetting("Scheduling:WorkerCount", "1")
            .UseSetting("Storage:LegacyViewsDirectory", legacy));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _database.Dispose();
        _dir.Delete(recursive: true);
    }

    [Fact]
    public async Task Scheduling_CreatesPrefixedHangfireTablesInTheApplicationDatabase()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/maps");
        response.EnsureSuccessStatusCode();

        await using var connection = new MySqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name IN ('maps', 'HangfireJob');
            """;

        Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }
}
