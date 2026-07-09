using System.Globalization;
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

    public static DuckDbSession InMemory()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        return new DuckDbSession(connection, null);
    }

    /// <summary>File-backed session whose database and temp spill both live in <paramref name="workDirectory"/>.</summary>
    public static DuckDbSession OnDisk(string workDirectory)
    {
        string dbPath = Path.Combine(workDirectory, "reduce.duckdb");
        DeleteDatabaseFiles(dbPath);
        var connection = new DuckDBConnection($"Data Source={Sql.Path(dbPath)}");
        connection.Open();
        var session = new DuckDbSession(connection, dbPath);
        session.Exec("SET preserve_insertion_order = false");
        session.Exec($"SET temp_directory = '{Sql.Path(workDirectory)}'");
        return session;
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
