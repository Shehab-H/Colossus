using System.Globalization;
using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.DuckDb;
using Xunit;

namespace Colossus.Tests;

/// <summary>Round-trips the fact grouper through in-memory DuckDB: a synthetic facts parquet is grouped
/// to marks, then read back. Pins one-mark-per-geometry, the measure SQL rendered from the AST (flat +
/// argmax), and the perMark/perFact classification — no ClickHouse.</summary>
public class FactGrouperTests
{
    private static ViewConfig View() => new()
    {
        Id = "mobile-dominance",
        Viewport = Viewport.Geo,
        Mark = Mark.Polygon,
        Source = new SourceSpec
        {
            Query = "SELECT * FROM t",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = new[]
            {
                new ChannelSpec { Name = "operator", Column = "operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "quarter", Column = "quarter", Role = ChannelRole.Temporal, Type = ChannelType.Date },
                new ChannelSpec { Name = "region", Column = "region", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "tests", Column = "tests", Role = ChannelRole.Measure, Type = ChannelType.F32 },
                new ChannelSpec { Name = "download_mbps", Column = "download_mbps", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            },
        },
        Measures = new[]
        {
            new MeasureSpec { Name = "total_tests", Expr = "sum(tests)" },
            new MeasureSpec { Name = "avg_download", Expr = "wavg(download_mbps, tests)" },
            new MeasureSpec { Name = "apex_share", Expr = "share(sum(tests)) where operator = 'apex'" },
            new MeasureSpec { Name = "dominant_operator", Expr = "argmax(operator, sum(tests))" },
        },
    };

    [Fact]
    public void GroupToMarks_OneMarkPerGeometry_WithMeasuresAndClassification()
    {
        string dir = Path.Combine(Path.GetTempPath(), "colossus-grouper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string factsPath = Path.Combine(dir, "facts.parquet");
            string marksPath = Path.Combine(dir, "marks.parquet");
            WriteFacts(factsPath);

            var grouping = new DuckDbFactGrouper().GroupToMarks(factsPath, marksPath, View());

            Assert.Equal(new[] { "region" }, grouping.PerMarkChannels);
            Assert.Equal(
                new HashSet<string> { "operator", "quarter", "tests", "download_mbps" },
                grouping.PerFactChannels.ToHashSet());

            using var db = DuckDbSession.InMemory();
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText =
                $"SELECT id, x, region, total_tests, avg_download, apex_share, dominant_operator, len(geometry) " +
                $"FROM read_parquet('{Sql.Path(marksPath)}') ORDER BY x";
            using var r = cmd.ExecuteReader();

            Assert.True(r.Read());
            string idA = r.GetString(0);
            Assert.Equal(1.0, D(r, 1), 3);
            Assert.Equal("west", r.GetString(2));                 // perMark first()
            Assert.Equal(18.0, D(r, 3), 3);                       // sum(tests)
            Assert.Equal(970.0 / 18.0, D(r, 4), 3);               // wavg(download_mbps, tests)
            Assert.Equal(15.0 / 18.0, D(r, 5), 3);                // share apex tests
            Assert.Equal("apex", r.GetString(6));                 // argmax operator by sum(tests)
            Assert.Equal(10L, Convert.ToInt64(r.GetValue(7)));    // geometry ring carried (first)

            Assert.True(r.Read());
            string idB = r.GetString(0);
            Assert.Equal("east", r.GetString(2));
            Assert.Equal(8.0, D(r, 3), 3);
            Assert.Equal(20.0, D(r, 4), 3);
            Assert.Equal(0.0, D(r, 5), 3);                        // no apex facts → 0 share
            Assert.Equal("zenith", r.GetString(6));

            Assert.False(r.Read());                               // exactly two marks
            Assert.NotEqual(idA, idB);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static double D(System.Data.Common.DbDataReader r, int i) =>
        Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);

    private static void WriteFacts(string path)
    {
        using var db = DuckDbSession.InMemory();
        db.Exec("""
            CREATE TABLE t (
              x FLOAT, y FLOAT, geometry FLOAT[], part_offsets INTEGER[],
              operator VARCHAR, quarter DATE, region VARCHAR, tests FLOAT, download_mbps FLOAT
            )
            """);
        db.Exec("""
            INSERT INTO t VALUES
              (1,1, [1,1,2,1,2,2,1,2,1,1], [0,5], 'apex',   DATE '2025-01-01', 'west', 10, 50),
              (1,1, [1,1,2,1,2,2,1,2,1,1], [0,5], 'apex',   DATE '2025-04-01', 'west',  5, 40),
              (1,1, [1,1,2,1,2,2,1,2,1,1], [0,5], 'zenith', DATE '2025-01-01', 'west',  3, 90),
              (2,2, [2,2,3,2,3,3,2,3,2,2], [0,5], 'zenith', DATE '2025-01-01', 'east',  8, 20)
            """);
        db.Exec($"COPY t TO '{Sql.Path(path)}' (FORMAT PARQUET)");
    }
}
