using Colossus.Domain.Baking;
using Colossus.Infrastructure.DuckDb;

namespace Colossus.Infrastructure.Baking;

/// <summary>Counts staged parquet rows from the file's own metadata — no scan.</summary>
public sealed class DuckDbStagingReader : IStagingReader
{
    public bool Exists(string stagingPath) => File.Exists(stagingPath);

    public long RowCount(string stagingPath)
    {
        using var db = DuckDbSession.InMemory();
        return db.Scalar($"SELECT count(*) FROM read_parquet('{Sql.Path(stagingPath)}')");
    }
}
