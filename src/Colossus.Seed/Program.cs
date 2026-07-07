using System.Globalization;
using Colossus.Core;

// Colossus.Seed — generates two synthetic datasets straight inside ClickHouse (INSERT…SELECT from
// numbers(), so no 800MB round-trip): a clustered geographic point set and a structured non-geo x/y
// cloud. Both feed the same bake engine, proving it is not geospatial-specific.

long n = 20_000_000;
if (args.Length > 0 && long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
    n = parsed;

using var ch = new ClickHouseClient();
Console.WriteLine("Waiting for ClickHouse…");
await ch.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));

await ch.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS {Views.Database}");

// --- Geo: clustered around 10 metro centers, with per-point gaussian scatter ---
Console.WriteLine($"Seeding {n:N0} geo points → {Views.Geo.Source.Table} …");
await ch.ExecuteAsync($"DROP TABLE IF EXISTS {Views.Geo.Source.Table}");
await ch.ExecuteAsync($"""
    CREATE TABLE {Views.Geo.Source.Table}
    (lon Float64, lat Float64, value Float32, category UInt8)
    ENGINE = MergeTree ORDER BY tuple()
    """);
await ch.ExecuteAsync($"""
    INSERT INTO {Views.Geo.Source.Table}
    WITH
      [-74.0,-0.12,139.69,2.35,-118.24,116.4,-43.2,151.2,77.2,-99.13] AS lonC,
      [40.71,51.5,35.68,48.85,34.05,39.9,-22.9,-33.87,28.61,19.43]    AS latC,
      toUInt8(number % 10) + 1 AS c
    SELECT
      greatest(-180, least(180, lonC[c] + randNormal(0, 4)))  AS lon,
      greatest(-85,  least(85,  latC[c] + randNormal(0, 2)))  AS lat,
      toFloat32(randUniform(0, 100))                          AS value,
      toUInt8(number % 8)                                     AS category
    FROM numbers({n})
    """);

// --- Non-geo: a structured x/y cloud (y correlated with x plus noise) ---
Console.WriteLine($"Seeding {n:N0} scatter points → {Views.Scatter.Source.Table} …");
await ch.ExecuteAsync($"DROP TABLE IF EXISTS {Views.Scatter.Source.Table}");
await ch.ExecuteAsync($"""
    CREATE TABLE {Views.Scatter.Source.Table}
    (x Float32, y Float32, value Float32, category UInt8)
    ENGINE = MergeTree ORDER BY tuple()
    """);
await ch.ExecuteAsync($"""
    INSERT INTO {Views.Scatter.Source.Table}
    WITH toFloat64(number) / {n} * 1000000 AS xx
    SELECT
      toFloat32(xx)                                AS x,
      toFloat32(0.6 * xx + randNormal(0, 50000))   AS y,
      toFloat32(randUniform(0, 100))               AS value,
      toUInt8(number % 8)                          AS category
    FROM numbers({n})
    """);

foreach (var v in Views.All)
{
    string count = (await ch.QueryTextAsync($"SELECT count() FROM {v.Source.Table} FORMAT TabSeparated")).Trim();
    Console.WriteLine($"  {v.Source.Table}: {count} rows");
}

Console.WriteLine("Seed complete.");
