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
}
