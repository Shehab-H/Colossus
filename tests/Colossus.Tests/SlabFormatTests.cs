using System.Text.Json;
using Colossus.Domain.Measures;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the C# slab writer against the shared cross-language fixture
/// (tests/fixtures/slab-cases.json, docs/companion-scale/SLAB-FORMAT.md). Builds the fixture facts into a
/// slab through the real <see cref="SlabCompanionWriter"/> (both layouts), reads the blocks back, and
/// asserts the CSR structure, the dense cumulative planes, and the fact witness. The TS side
/// (web/src/lib/slab.test.ts) pins the same fixture's fold.</summary>
public class SlabFormatTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-slab-");
    private static readonly JsonDocument Fixture = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(FindRepoRoot(), "tests", "fixtures", "slab-cases.json")));

    public void Dispose() => _dir.Delete(recursive: true);

    private static readonly List<Partial> Partials =
    [
        new(PartialKind.Sum, "tests"),
        new(PartialKind.Count),
        new(PartialKind.Swp, "download_mbps", "tests"),
        new(PartialKind.Max, "tests"),
    ];

    // Axes in cell order: operator (categorical, stride 3) outer, quarter (ordered, stride 1) fastest/cumulative.
    private static SlabPlan Plan(bool dense) => new()
    {
        Dense = dense,
        Cells = 6,
        Occupancy = 5.0 / 12,
        Axes =
        [
            new SlabAxisPlan("operator", true, ["apex", "zenith"], 3, false),
            new SlabAxisPlan("quarter", false, ["2025-01-01", "2025-04-01", "2025-07-01"], 1, true),
        ],
        Partials = Partials,
        CumulativeStride = 1,
        CumulativeCardinality = 3,
    };

    private SlabTile BuildAndRead(bool dense)
    {
        string values = string.Join(",\n", Fixture.RootElement.GetProperty("facts").EnumerateArray().Select(f =>
            $"({f.GetProperty("mki").GetInt32()}, '{f.GetProperty("operator").GetString()}', " +
            $"DATE '{f.GetProperty("quarter").GetString()}', {f.GetProperty("tests").GetDouble()}::FLOAT, " +
            $"{f.GetProperty("download_mbps").GetDouble()}::FLOAT)"));

        var plan = Plan(dense);
        using var db = DuckDbSession.InMemory();
        db.Exec($"CREATE TABLE facts (mki INTEGER, operator VARCHAR, quarter DATE, tests FLOAT, download_mbps FLOAT)");
        db.Exec($"INSERT INTO facts VALUES {values}");

        // Mirror the reducer's stream form: (tx, ty, mki, cellId, partials…), grouped to grain, mki-major.
        string sql = $"""
            SELECT 0 AS tx, 0 AS ty, mki, ({plan.CellIdSql()}) AS cellId,
                   sum(tests)::FLOAT AS "sum__tests", count(*)::INTEGER AS "cnt",
                   sum(download_mbps * tests)::FLOAT AS "swp__download_mbps__tests", max(tests)::FLOAT AS "max__tests"
            FROM facts GROUP BY mki, operator, quarter ORDER BY mki, cellId
            """;

        using (var writer = new SlabCompanionWriter(_dir.FullName, plan))
        {
            writer.AppendLevel(0, db.Connection, sql, new Dictionary<(long, long), long> { [(0, 0)] = 2 });
            var pack = writer.Finish();
            Assert.Equal("slab", pack.Format);
            Assert.True(pack.PlaneEntries!.ContainsKey("0/0/0"));
            return SlabCompanionReader.Read(Path.Combine(_dir.FullName, pack.File), pack.PlaneEntries["0/0/0"], plan.ToManifest());
        }
    }

    [Fact]
    public void Sparse_CsrEncoding_MatchesFixture()
    {
        var tile = BuildAndRead(dense: false);
        var sp = Fixture.RootElement.GetProperty("sparse");

        Assert.Equal(Ints(sp.GetProperty("offsets")), tile.Offsets);
        Assert.Equal(Ints(sp.GetProperty("cellIds")), tile.CellIds);
        var planes = sp.GetProperty("planes");
        Assert.Equal(Floats(planes.GetProperty("sum__tests")), tile.FloatPlanes["sum__tests"]);
        Assert.Equal(Ints(planes.GetProperty("cnt")), tile.IntPlanes["cnt"]);
        Assert.Equal(Floats(planes.GetProperty("swp__download_mbps__tests")), tile.FloatPlanes["swp__download_mbps__tests"]);
        Assert.Equal(Floats(planes.GetProperty("max__tests")), tile.FloatPlanes["max__tests"]);

        Assert.Equal(5, SlabCompanionReader.Facts(tile, Plan(false).ToManifest())); // nnz witness
    }

    [Fact]
    public void Dense_CumulativeCellMajor_MatchesFixture()
    {
        var tile = BuildAndRead(dense: true);
        var planes = Fixture.RootElement.GetProperty("dense").GetProperty("planes");

        Assert.Equal(Floats(planes.GetProperty("sum__tests")), tile.FloatPlanes["sum__tests"]);
        Assert.Equal(Ints(planes.GetProperty("cnt")), tile.IntPlanes["cnt"]);
        Assert.Equal(Floats(planes.GetProperty("swp__download_mbps__tests")), tile.FloatPlanes["swp__download_mbps__tests"]);
        AssertFloatsWithNaN(planes.GetProperty("max__tests"), tile.FloatPlanes["max__tests"]);

        Assert.Equal(5, SlabCompanionReader.Facts(tile, Plan(true).ToManifest())); // Σ cnt via last-bin
    }

    private static int[] Ints(JsonElement a) => [.. a.EnumerateArray().Select(e => e.GetInt32())];
    private static float[] Floats(JsonElement a) => [.. a.EnumerateArray().Select(e => (float)e.GetDouble())];

    private static void AssertFloatsWithNaN(JsonElement expected, float[] actual)
    {
        var e = expected.EnumerateArray().ToArray();
        Assert.Equal(e.Length, actual.Length);
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i].ValueKind == JsonValueKind.Null) Assert.True(float.IsNaN(actual[i]), $"cell {i} expected NaN");
            else Assert.Equal((float)e[i].GetDouble(), actual[i], 3);
        }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Colossus.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root (Colossus.slnx) not found");
    }
}
