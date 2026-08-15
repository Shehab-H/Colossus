using Colossus.Application.Abstractions.Baking;
using MySqlConnector;

namespace Colossus.Infrastructure.Catalog;

/// <summary>Serializes due-policy evaluation per map across application replicas.</summary>
public sealed class MySqlBakeScheduleLock(string connectionString) : IBakeScheduleLock
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid mapId, CancellationToken ct = default)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        string name = $"colossus:schedule:{mapId:N}";
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name, 0);";
        command.Parameters.AddWithValue("@name", name);
        object? acquired = await command.ExecuteScalarAsync(ct);
        if (Convert.ToInt32(acquired) == 1) return new Lease(connection, name);
        await connection.DisposeAsync();
        return null;
    }

    private sealed class Lease(MySqlConnection connection, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT RELEASE_LOCK(@name);";
                command.Parameters.AddWithValue("@name", name);
                _ = await command.ExecuteScalarAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
