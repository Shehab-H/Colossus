using System.Collections;
using System.Globalization;
using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.DuckDb;

namespace Colossus.Infrastructure.Baking;

/// <summary>Scans the staged extract once per channel with DuckDB: numeric channels get min/max plus a
/// quantile grid, everything else gets its distinct values (capped). Purely schema-driven — the channel
/// list and types come from the view config, never from the data's shape. A channel whose scan fails is
/// simply omitted; the client falls back to its root-tile derivation.</summary>
public sealed class DuckDbChannelDomainScanner : IChannelDomainScanner
{
    // Options lists and categorical color domains stop being useful long before this; past the cap the
    // domain is marked truncated and the client treats it as absent.
    private const int DistinctCap = 4096;
    // Quantile grid resolution: enough for any client bin count while staying a few hundred bytes.
    private const int QuantilePoints = 129;

    private static readonly HashSet<ChannelType> Numeric =
        [ChannelType.F32, ChannelType.F64, ChannelType.U8, ChannelType.U16, ChannelType.I32, ChannelType.I64];

    public IReadOnlyDictionary<string, ChannelDomain> Scan(string stagingParquetPath, ViewConfig view)
    {
        var domains = new Dictionary<string, ChannelDomain>(StringComparer.Ordinal);
        if (view.Source.Channels.Count == 0) return domains;

        using var db = DuckDbSession.InMemory();
        db.Exec($"CREATE VIEW v AS SELECT * FROM read_parquet('{Sql.Path(stagingParquetPath)}')");

        foreach (var ch in view.Source.Channels)
        {
            try
            {
                var domain = Numeric.Contains(ch.Type) ? ScanNumeric(db, ch.Name) : ScanDistinct(db, ch);
                if (domain is not null) domains[ch.Name] = domain;
            }
            catch
            {
                // Best-effort per channel: a missing/odd column must not fail the bake.
            }
        }
        return domains;
    }

    private static ChannelDomain? ScanNumeric(DuckDbSession db, string name)
    {
        string col = $"{Quote(name)}::DOUBLE";
        string grid = string.Join(", ", Enumerable.Range(0, QuantilePoints)
            .Select(i => ((double)i / (QuantilePoints - 1)).ToString("R", CultureInfo.InvariantCulture)));

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT min({col}), max({col}), quantile_cont({col}, [{grid}]) FROM v";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return null; // empty or all-null column

        var quantiles = new List<double>();
        if (!reader.IsDBNull(2))
            foreach (object? q in (IEnumerable)reader.GetValue(2))
                if (q is not null) quantiles.Add(Convert.ToDouble(q, CultureInfo.InvariantCulture));

        return new ChannelDomain
        {
            Min = reader.GetDouble(0),
            Max = reader.GetDouble(1),
            Quantiles = quantiles.Count > 0 ? quantiles : null,
        };
    }

    /// <summary>Distinct values as the SAME strings the client's filter SQL compares against
    /// (web/src/lib/channels.ts channelSqlExpr): temporal values normalize to YYYY-MM-DD whether the
    /// staged column is a real DATE or day-integers; everything else is a plain VARCHAR cast. Nulls
    /// surface as the literal "null", matching what a tile scan produced.
    /// Temporal channels store just [min, max]: the date control's whole contract is a range (its
    /// bounds and the "latest" default), so the full distinct list would only risk the cap.</summary>
    private static ChannelDomain? ScanDistinct(DuckDbSession db, ChannelSpec ch)
    {
        bool temporal = ch.Role == ChannelRole.Temporal || ch.Type == ChannelType.Date;
        string expr = temporal ? TemporalExpr(db, ch.Name) : $"{Quote(ch.Name)}::VARCHAR";

        var values = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = temporal
            ? $"SELECT DISTINCT r FROM (SELECT min({expr}) AS r FROM v UNION ALL SELECT max({expr}) FROM v) WHERE r IS NOT NULL"
            : $"SELECT DISTINCT {expr} FROM v LIMIT {DistinctCap + 1}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) values.Add(reader.IsDBNull(0) ? "null" : reader.GetString(0));

        if (temporal && values.Count == 0) return null; // empty or all-null column
        if (values.Count > DistinctCap) return new ChannelDomain { ValuesTruncated = true };
        values.Sort(StringComparer.Ordinal); // JS Array.sort() order, so the client sees identical lists
        return new ChannelDomain { Values = values };
    }

    private static string TemporalExpr(DuckDbSession db, string name)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT {Quote(name)} FROM v";
        using var reader = cmd.ExecuteReader();
        string type = reader.Read() ? reader.GetString(1) : "";
        return type.Contains("DATE", StringComparison.OrdinalIgnoreCase) || type.Contains("TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            ? $"strftime({Quote(name)}::DATE, '%Y-%m-%d')"
            : $"strftime(DATE '1970-01-01' + {Quote(name)}::INTEGER, '%Y-%m-%d')"; // day-count storage
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
