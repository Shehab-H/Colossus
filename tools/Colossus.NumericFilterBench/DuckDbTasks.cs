using System.Diagnostics;
using DuckDB.NET.Data;

namespace Colossus.NumericFilterBench;

internal static class DuckDbTasks
{
    public static async Task InspectAsync(string parquet)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DESCRIBE SELECT * FROM read_parquet($path)";
        command.Parameters.Add(new DuckDBParameter("path", parquet));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            Console.WriteLine($"{reader.GetString(0),-32} {reader.GetString(1)}");
    }

    public static async Task ProfileAsync(string parquet)
    {
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH mark_counts AS (
              SELECT x, y, count(*) AS fact_count
              FROM read_parquet($path)
              GROUP BY x, y
            )
            SELECT
              (SELECT count(*) FROM read_parquet($path)) AS facts,
              count(*) AS marks,
              max(fact_count) AS max_facts_per_mark,
              avg(fact_count) AS avg_facts_per_mark
            FROM mark_counts
            """;
        command.Parameters.Add(new DuckDBParameter("path", parquet));
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Console.WriteLine($"facts={reader.GetInt64(0):N0}");
        Console.WriteLine($"marks={reader.GetInt64(1):N0}");
        Console.WriteLine($"maxFactsPerMark={reader.GetInt64(2):N0}");
        Console.WriteLine($"avgFactsPerMark={reader.GetDouble(3):N3}");
    }

    public static async Task PrepareTimestampSortedShardAsync(
        string parquet, string output, string operatorValue, int quarter, int threads)
    {
        if (File.Exists(output))
            throw new IOException($"Refusing to overwrite existing Parquet file: {output}");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        var wall = Stopwatch.StartNew();
        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await ExecAsync(connection, $"SET threads = {threads}");
        await ExecAsync(connection, "SET memory_limit = '4GB'");
        await ExecAsync(connection, "SET preserve_insertion_order = false");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            COPY (
              SELECT *
              FROM read_parquet($path)
              WHERE operator = $operator AND quarter = $quarter
              ORDER BY
                CAST(quarter AS BIGINT) * 7889400 +
                ABS(CAST(download_mbps * 1000.0 AS BIGINT) * 31 +
                    CAST(upload_mbps * 1000.0 AS BIGINT) * 17 +
                    CAST(latency_ms * 1000.0 AS BIGINT) * 13 +
                    CAST(tests * 1000.0 AS BIGINT) * 7) % 7889400
            ) TO $output (FORMAT PARQUET, COMPRESSION ZSTD, ROW_GROUP_SIZE 65536)
            """;
        command.Parameters.Add(new DuckDBParameter("path", parquet));
        command.Parameters.Add(new DuckDBParameter("operator", operatorValue));
        command.Parameters.Add(new DuckDBParameter("quarter", quarter));
        command.Parameters.Add(new DuckDBParameter("output", output));
        await command.ExecuteNonQueryAsync();
        wall.Stop();
        process.Refresh();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine(
            $"operation=prepare-timestamp-sorted-shard operator={operatorValue} quarter={quarter} " +
            $"threads={threads} wallMs={wall.Elapsed.TotalMilliseconds:F2} cpuMs={cpuMs:F2} " +
            $"effectiveCores={cpuMs / wall.Elapsed.TotalMilliseconds:F2} " +
            $"processReadMiB={(read1 - read0) / 1048576.0:F2} " +
            $"processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2} " +
            $"artifactMiB={new FileInfo(output).Length / 1048576.0:F2}");
    }

    public static async Task VerifyTimestampCellAsync(
        string parquet, string operatorValue, int quarter)
    {
        const long quarterSeconds = 7_889_400;
        long quarterLow = quarter * quarterSeconds;
        long quarterHigh = checked((quarter + 1L) * quarterSeconds - 1);
        foreach ((string name, long low, long high) in new[]
        {
            ("wide", quarterLow + 123, quarterHigh - 456),
            ("middle", quarterLow + quarterSeconds / 4, quarterLow + quarterSeconds * 3 / 4)
        })
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await ExecAsync(connection, "SET threads = 12");
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT x, y,
                       CAST(SUM(CAST(ROUND(CAST(download_mbps AS DOUBLE) * 1000.0) AS BIGINT) *
                                CAST(ROUND(CAST(tests AS DOUBLE) * 1000.0) AS BIGINT)) AS BIGINT) AS numerator,
                       CAST(SUM(CAST(ROUND(CAST(tests AS DOUBLE) * 1000.0) AS BIGINT)) AS BIGINT) AS denominator
                FROM read_parquet($path)
                WHERE operator = $operator AND quarter = $quarter
                  AND CAST(CAST(quarter AS BIGINT) * 7889400 +
                           ABS(CAST(TRUNC(download_mbps * 1000.0) AS BIGINT) * 31 +
                               CAST(TRUNC(upload_mbps * 1000.0) AS BIGINT) * 17 +
                               CAST(TRUNC(latency_ms * 1000.0) AS BIGINT) * 13 +
                               CAST(TRUNC(tests * 1000.0) AS BIGINT) * 7) % 7889400
                           AS BIGINT) BETWEEN $low AND $high
                GROUP BY x, y
                """;
            command.Parameters.Add(new DuckDBParameter("path", parquet));
            command.Parameters.Add(new DuckDBParameter("operator", operatorValue));
            command.Parameters.Add(new DuckDBParameter("quarter", quarter));
            command.Parameters.Add(new DuckDBParameter("low", low));
            command.Parameters.Add(new DuckDBParameter("high", high));
            var wall = Stopwatch.StartNew();
            await using var reader = await command.ExecuteReaderAsync();
            ulong rows = 0;
            ulong checksum = 0;
            while (await reader.ReadAsync())
            {
                float x = reader.GetFloat(0);
                float y = reader.GetFloat(1);
                long numerator = reader.GetInt64(2);
                long denominator = reader.GetInt64(3);
                ulong mark = ((ulong)BitConverter.SingleToUInt32Bits(x) << 32) |
                             BitConverter.SingleToUInt32Bits(y);
                checksum ^= Mix64(mark ^ Mix64((ulong)numerator) ^ Mix64((ulong)denominator));
                rows++;
            }
            Console.WriteLine(
                $"oracle=duckdb query={name} rows={rows} checksum=0x{checksum:X16} " +
                $"wallMs={wall.Elapsed.TotalMilliseconds:F2}");
        }
    }

    private static ulong Mix64(ulong value)
    {
        value = unchecked(value + 0x9E3779B97F4A7C15UL);
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return value ^ (value >> 31);
    }

    public static async Task BuildServingDatabaseAsync(string parquet, string database, bool indexes)
    {
        if (File.Exists(database))
            throw new IOException($"Refusing to overwrite existing database: {database}");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);

        var sw = Stopwatch.StartNew();
        await using var connection = new DuckDBConnection($"DataSource={database}");
        await connection.OpenAsync();
        await ExecAsync(connection, "SET threads = 12");
        await ExecAsync(connection, "SET memory_limit = '12GB'");
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TEMP TABLE mark_map AS
                WITH firsts AS (
                  SELECT x, y, min(file_row_number) AS first_row
                  FROM read_parquet($path, file_row_number = true)
                  GROUP BY x, y
                )
                SELECT x, y,
                       (row_number() OVER (ORDER BY first_row) - 1)::UINTEGER AS mark_id
                FROM firsts;

                CREATE TABLE facts AS
                SELECT m.mark_id, f.tests, f.sample_share, f.download_mbps,
                       f.upload_mbps, f.latency_ms
                FROM read_parquet($path, file_row_number = true) f
                JOIN mark_map m USING (x, y)
                ORDER BY m.mark_id, f.file_row_number
                """;
            command.Parameters.Add(new DuckDBParameter("path", parquet));
            await command.ExecuteNonQueryAsync();
        }

        if (indexes)
        {
            foreach (string field in Fields.Names)
            {
                Console.WriteLine($"creating ART index for {field}");
                await ExecAsync(connection, $"CREATE INDEX idx_{field} ON facts ({field})");
            }
        }
        await ExecAsync(connection, "CHECKPOINT");
        sw.Stop();
        Console.WriteLine($"duckdbBuildSeconds={sw.Elapsed.TotalSeconds:F2}");
        Console.WriteLine($"duckdbBytes={new FileInfo(database).Length}");
    }

    public static async Task BenchmarkServingDatabaseAsync(string database, string artifactDirectory)
    {
        ArtifactManifest manifest = ArtifactManifest.Read(artifactDirectory);
        RangeBox[] boxes = BenchmarkRunner.Cases(manifest);
        await using var connection = new DuckDBConnection($"DataSource={database}");
        await connection.OpenAsync();
        await ExecAsync(connection, "SET threads = 12");
        await ExecAsync(connection, "SET memory_limit = '12GB'");

        foreach (RangeBox box in boxes)
        {
            string predicate = PredicateSql(box);
            string sql = $"""
                SELECT mark_id,
                       CAST(sum(CAST(download_mbps * tests AS DOUBLE)) /
                            sum(CAST(tests AS DOUBLE)) AS FLOAT) AS value
                FROM facts
                WHERE {predicate}
                GROUP BY mark_id
                """;
            await ExecuteAndMeasureAsync(connection, box.Name, sql, warmup: true);
        }
    }

    public static async Task PressureServingDatabaseAsync(
        string database, string artifactDirectory, int users)
    {
        ProcessMetrics.PinToSixCores();
        ArtifactManifest manifest = ArtifactManifest.Read(artifactDirectory);
        RangeBox outer = RangeBox.Quantile(manifest, 25, 75);
        RangeBox inner = RangeBox.Quantile(manifest, 45, 55);
        var boxes = new RangeBox[users];
        for (int user = 0; user < users; user++)
        {
            float fraction = (user + 1f) / (users + 1f) * 0.10f;
            float[] low = new float[Fields.Names.Length];
            float[] high = new float[Fields.Names.Length];
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                low[d] = outer.Low[d] + (inner.Low[d] - outer.Low[d]) * fraction;
                high[d] = outer.High[d] - (outer.High[d] - inner.High[d]) * fraction;
            }
            boxes[user] = new RangeBox(low, high, $"unique-middle-{user}");
        }

        await using (var warm = new DuckDBConnection($"DataSource={database}"))
        {
            await warm.OpenAsync();
            await ExecAsync(warm, "SET threads = 12");
            await ConsumeAsync(warm, QuerySql(outer));
        }

        var connections = new DuckDBConnection[users];
        for (int user = 0; user < users; user++)
        {
            connections[user] = new DuckDBConnection($"DataSource={database}");
            await connections[user].OpenAsync();
        }

        var barrier = new Barrier(users + 1);
        var latency = new double[users];
        var rows = new long[users];
        var threads = new Thread[users];
        for (int user = 0; user < users; user++)
        {
            int local = user;
            threads[user] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var wall = Stopwatch.StartNew();
                (long resultRows, _) = ConsumeAsync(connections[local], QuerySql(boxes[local]))
                    .GetAwaiter().GetResult();
                wall.Stop();
                latency[local] = wall.Elapsed.TotalMilliseconds;
                rows[local] = resultRows;
            }) { IsBackground = true, Name = $"duckdb-user-{user}" };
            threads[user].Start();
        }

        var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        barrier.SignalAndWait();
        var burst = Stopwatch.StartNew();
        foreach (Thread thread in threads) thread.Join();
        burst.Stop();
        process.Refresh();
        Array.Sort(latency);
        Console.WriteLine(
            $"engine=duckdb-pressure users={users} throughputQps={users / burst.Elapsed.TotalSeconds:F2} " +
            $"burstWallMs={burst.Elapsed.TotalMilliseconds:F2} p50Ms={Percentile(latency, .50):F2} " +
            $"p95Ms={Percentile(latency, .95):F2} p99Ms={Percentile(latency, .99):F2} " +
            $"rows={rows.Sum()} cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} " +
            $"effectiveCores={(process.TotalProcessorTime - cpu0).TotalSeconds / burst.Elapsed.TotalSeconds:F2} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");

        foreach (DuckDBConnection connection in connections)
            await connection.DisposeAsync();
    }

    private static string PredicateSql(RangeBox box)
    {
        var parts = new string[Fields.Names.Length];
        for (int d = 0; d < parts.Length; d++)
        {
            parts[d] = FormattableString.Invariant(
                $"{Fields.Names[d]} BETWEEN {box.Low[d]:R} AND {box.High[d]:R}");
        }
        return string.Join(" AND ", parts);
    }

    private static string QuerySql(RangeBox box) => $"""
        SELECT mark_id,
               CAST(sum(CAST(download_mbps * tests AS DOUBLE)) /
                    sum(CAST(tests AS DOUBLE)) AS FLOAT) AS value
        FROM facts
        WHERE {PredicateSql(box)}
        GROUP BY mark_id
        """;

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        int index = Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static async Task ExecuteAndMeasureAsync(
        DuckDBConnection connection, string name, string sql, bool warmup)
    {
        if (warmup)
            await ConsumeAsync(connection, sql);

        var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        var sw = Stopwatch.StartNew();
        (long rows, ulong checksum) = await ConsumeAsync(connection, sql);
        sw.Stop();
        process.Refresh();
        Console.WriteLine(
            $"engine=duckdb query={name} wallMs={sw.Elapsed.TotalMilliseconds:F2} " +
            $"cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} rows={rows} " +
            $"checksum=0x{checksum:X16} workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2}");
    }

    private static async Task<(long Rows, ulong Checksum)> ConsumeAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        long rows = 0;
        ulong checksum = 0;
        while (await reader.ReadAsync())
        {
            uint mark = unchecked((uint)reader.GetInt64(0));
            float value = reader.GetFloat(1);
            checksum ^= BenchmarkHash.MarkValue(mark, value);
            rows++;
        }
        return (rows, checksum);
    }

    private static async Task ExecAsync(DuckDBConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
