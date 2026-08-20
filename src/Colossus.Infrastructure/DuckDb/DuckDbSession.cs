using System.Globalization;
using Colossus.Infrastructure.Configuration;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.DuckDb;

/// <summary>One DuckDB connection scoped to a reduction run. A file-backed session works out-of-core
/// (bounded by disk, not RAM) and removes its database files on dispose.</summary>
public sealed class DuckDbSession : IDisposable
{
    private readonly string? _dbPath;

    public DuckDBConnection Connection { get; }

    private DuckDbSession(DuckDBConnection connection, string? dbPath)
    {
        Connection = connection;
        _dbPath = dbPath;
    }

    public static DuckDbSession InMemory(BakeResourceOptions? resources = null)
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        var session = new DuckDbSession(connection, null);
        session.Apply(resources);
        return session;
    }

    /// <summary>File-backed session whose database and temp spill both live in <paramref name="workDirectory"/>.</summary>
    public static DuckDbSession OnDisk(string workDirectory, BakeResourceOptions? resources = null)
    {
        Directory.CreateDirectory(workDirectory);
        string dbPath = Path.Combine(workDirectory, $"reduce-{Guid.NewGuid():N}.duckdb");
        DeleteDatabaseFiles(dbPath);
        var connection = new DuckDBConnection($"Data Source={Sql.Path(dbPath)}");
        connection.Open();
        var session = new DuckDbSession(connection, dbPath);
        session.Exec("SET preserve_insertion_order = false");
        session.Exec($"SET temp_directory = '{Sql.Path(workDirectory)}'");
        session.Apply(resources);
        return session;
    }

    private void Apply(BakeResourceOptions? resources)
    {
        if (resources?.CpuCores > 0) Exec($"SET threads = {resources.CpuCores}");
        if (resources?.DuckDbRamMb > 0) Exec($"SET memory_limit = '{resources.DuckDbRamMb}MB'");
    }

    public void Exec(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long Scalar(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The query's first two BIGINT columns as a set of pairs.</summary>
    public HashSet<(long, long)> LongPairs(string sql)
    {
        var set = new HashSet<(long, long)>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return set;
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (_dbPath is not null) DeleteDatabaseFiles(_dbPath);
    }

    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (string p in new[] { dbPath, dbPath + ".wal" })
            if (File.Exists(p)) File.Delete(p);
    }
}
