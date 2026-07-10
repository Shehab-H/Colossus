using System.Text.Json;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiling;
using Xunit;
using Axis = Colossus.Infrastructure.Tiling.TileSql.Axis;

namespace Colossus.Tests;

/// <summary>Pins the tiling scheme to one spec across every implementation. The shared fixture
/// (<c>tests/fixtures/tiling-cases.json</c>, also read by the web Vitest suite) lists point→tile cases;
/// here we assert both <see cref="TileMath.PointToTile"/> (C#) and <see cref="TileSql"/> (the DuckDB SQL
/// projection) reproduce them. If any drifts, this fails.</summary>
public class TilingConformanceTests
{
    private sealed record Fixture(FixtureRoot Root, IReadOnlyList<Case> Cases);
    private sealed record FixtureRoot(double MinX, double MinY, double MaxX, double MaxY);
    private sealed record Case(int Z, double Px, double Py, int TileX, int TileY);

    private static Fixture Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "tiling-cases.json");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), opts)!;
    }

    private static Bbox Root(Fixture f) => new(f.Root.MinX, f.Root.MinY, f.Root.MaxX, f.Root.MaxY);

    [Fact]
    public void TileMath_MatchesEveryFixtureCase()
    {
        var f = Load();
        Assert.NotEmpty(f.Cases);
        var root = Root(f);
        foreach (var c in f.Cases)
        {
            var (x, y) = TileMath.PointToTile(root, c.Z, c.Px, c.Py);
            Assert.True(x == c.TileX && y == c.TileY,
                $"TileMath z={c.Z} ({c.Px},{c.Py}) → ({x},{y}), fixture ({c.TileX},{c.TileY})");
        }
    }

    [Fact]
    public void TileSql_RunThroughDuckDb_MatchesEveryFixtureCase()
    {
        var f = Load();
        var root = Root(f);
        using var db = DuckDbSession.InMemory();

        foreach (var c in f.Cases)
        {
            string sql =
                $"SELECT {TileSql.TileIndex(root, c.Z, Axis.X)} AS tx, {TileSql.TileIndex(root, c.Z, Axis.Y)} AS ty " +
                $"FROM (SELECT {Sql.Lit(c.Px)}::DOUBLE AS {TileSchema.X}, {Sql.Lit(c.Py)}::DOUBLE AS {TileSchema.Y})";
            var (tx, ty) = ReadPair(db, sql);
            Assert.True(tx == c.TileX && ty == c.TileY,
                $"TileSql z={c.Z} ({c.Px},{c.Py}) → ({tx},{ty}), fixture ({c.TileX},{c.TileY})");
        }
    }

    private static (long, long) ReadPair(DuckDbSession db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // The fixture cases all sit inside tiles. A seam is where TileSql and TileMath can disagree by a cell,
    // since each rounds the boundary on its own; GeoNames' root is the bbox that exposed it.
    private const double GeoMin = -180.17999999999998, GeoMax = 180.17999999999998;
    private static readonly Bbox GeoRoot = new(GeoMin, GeoMin, GeoMax, GeoMax);

    [Fact]
    public void TileSql_TileIndex_MatchesTileMath_OnTileSeams()
    {
        var probes = SeamProbes();
        using var db = DuckDbSession.InMemory();
        CreateProbeTable(db, probes);

        for (int z = 0; z <= 6; z++)
        {
            string sql = $"SELECT i, {TileSql.TileIndex(GeoRoot, z, Axis.X)}, " +
                         $"{TileSql.TileIndex(GeoRoot, z, Axis.Y)} FROM probes ORDER BY i";
            foreach (var (i, tx, ty) in ReadTriples(db, sql))
            {
                long expected = TileMath.CellIndex(GeoMin, GeoMax, 1L << z, probes[(int)i]);
                Assert.True(tx == expected && ty == expected,
                    $"z={z} p={probes[(int)i]:R}: TileSql → ({tx},{ty}), TileMath → {expected}");
            }
        }
    }

    [Fact]
    public void TileSql_TileRectPredicate_MatchesTileMath_Contains()
    {
        var probes = SeamProbes();
        using var db = DuckDbSession.InMemory();
        CreateProbeTable(db, probes);

        for (int z = 0; z <= 3; z++)
            for (int x = 0; x < 1 << z; x++)
                for (int y = 0; y < 1 << z; y++)
                {
                    var tile = new TileId(z, x, y);
                    string sql = $"SELECT i FROM probes WHERE {TileSql.TileRectPredicate(GeoRoot, tile)} ORDER BY i";
                    var sqlHits = ReadLongs(db, sql).ToHashSet();
                    var mathHits = Enumerable.Range(0, probes.Count)
                        .Where(i => TileMath.Contains(GeoRoot, tile, probes[i], probes[i]))
                        .Select(i => (long)i).ToHashSet();
                    Assert.True(sqlHits.SetEquals(mathHits),
                        $"tile {tile.RelativePath}: SQL matched {sqlHits.Count} probes, TileMath {mathHits.Count}");
                }
    }

    /// <summary>Every tile boundary of the root down to z=6, plus the doubles either side of each.</summary>
    private static IReadOnlyList<double> SeamProbes()
    {
        var probes = new SortedSet<double>();
        for (int z = 1; z <= 6; z++)
            for (long i = 0; i <= 1L << z; i++)
            {
                double edge = TileMath.Edge(GeoMin, GeoMax, 1L << z, i);
                foreach (double p in new[] { Math.BitDecrement(edge), edge, Math.BitIncrement(edge) })
                    if (p >= GeoMin && p <= GeoMax) probes.Add(p);
            }
        return probes.ToList();
    }

    private static void CreateProbeTable(DuckDbSession db, IReadOnlyList<double> probes)
    {
        db.Exec($"CREATE TABLE probes (i BIGINT, {TileSchema.X} DOUBLE, {TileSchema.Y} DOUBLE)");
        string values = string.Join(", ", probes.Select((p, i) => $"({i}, {Sql.Lit(p)}, {Sql.Lit(p)})"));
        db.Exec($"INSERT INTO probes VALUES {values}");
    }

    private static IEnumerable<(long, long, long)> ReadTriples(DuckDbSession db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static IEnumerable<long> ReadLongs(DuckDbSession db, string sql)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return reader.GetInt64(0);
    }
}
