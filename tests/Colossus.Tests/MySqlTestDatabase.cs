using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Colossus.Tests;

internal sealed class MySqlTestDatabase : IDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _disposed;

    public MySqlTestDatabase()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        _adminConnectionString = configuration.GetConnectionString("DatabaseAdmin")
            ?? throw new InvalidOperationException("ConnectionStrings:DatabaseAdmin is required for database tests.");
        _databaseName = $"colossus_test_{Guid.NewGuid():N}";

        using var admin = new MySqlConnection(_adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{_databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;";
        command.ExecuteNonQuery();

        var catalog = new MySqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            Pooling = false,
        };
        ConnectionString = catalog.ConnectionString;
    }

    public string ConnectionString { get; }

    public void Dispose()
    {
        if (_disposed) return;
        MySqlConnection.ClearAllPools();
        using var admin = new MySqlConnection(_adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS `{_databaseName}`;";
        command.ExecuteNonQuery();
        _disposed = true;
    }
}
