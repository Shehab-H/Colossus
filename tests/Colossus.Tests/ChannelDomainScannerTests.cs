using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.DuckDb;
using Xunit;

namespace Colossus.Tests;

/// <summary>End-to-end over a real parquet extract: the scanner must produce exactly what the client
/// consumes from manifest.channelDomains (see web/src/lib/channels.ts).</summary>
public class ChannelDomainScannerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Staging(string sql)
    {
        string path = Path.Combine(_dir.FullName, "staging.parquet");
        using var db = DuckDbSession.InMemory();
        db.Exec($"COPY ({sql}) TO '{Sql.Path(path)}' (FORMAT PARQUET)");
        return path;
    }

    private static ViewConfig View(params ChannelSpec[] channels) => new()
    {
        Id = "scan-test",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Query = "q",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            Channels = channels,
        },
    };

    [Fact]
    public void NumericChannel_GetsMinMaxAndQuantiles()
    {
        string staging = Staging("SELECT x::FLOAT AS x, x::FLOAT AS y, x::DOUBLE AS pop FROM range(101) r(x)");
        var view = View(new ChannelSpec { Name = "pop", Column = "pop", Role = ChannelRole.Measure, Type = ChannelType.F64 });

        var d = new DuckDbChannelDomainScanner().Scan(staging, view)["pop"];
        Assert.Equal(0, d.Min);
        Assert.Equal(100, d.Max);
        Assert.NotNull(d.Quantiles);
        Assert.Equal(129, d.Quantiles!.Count);
        Assert.Equal(0, d.Quantiles[0]);
        Assert.Equal(100, d.Quantiles[^1]);
        Assert.Equal(50, d.Quantiles[64]); // median of 0..100
    }

    [Fact]
    public void DictChannel_GetsSortedDistinctValues_WithNullAsLiteral()
    {
        string staging = Staging(
            "SELECT x::FLOAT AS x, x::FLOAT AS y, CASE WHEN x = 0 THEN NULL WHEN x % 2 = 0 THEN 'b' ELSE 'a' END AS cat FROM range(10) r(x)");
        var view = View(new ChannelSpec { Name = "cat", Column = "cat", Role = ChannelRole.Dimension, Type = ChannelType.Dict });

        var d = new DuckDbChannelDomainScanner().Scan(staging, view)["cat"];
        Assert.Equal(["a", "b", "null"], d.Values);
        Assert.Null(d.ValuesTruncated);
    }

    [Fact]
    public void DictChannel_OverCap_MarksTruncated()
    {
        string staging = Staging("SELECT x::FLOAT AS x, x::FLOAT AS y, 'v' || x AS wide FROM range(5000) r(x)");
        var view = View(new ChannelSpec { Name = "wide", Column = "wide", Role = ChannelRole.Dimension, Type = ChannelType.Dict });

        var d = new DuckDbChannelDomainScanner().Scan(staging, view)["wide"];
        Assert.Null(d.Values);
        Assert.True(d.ValuesTruncated);
    }

    [Fact]
    public void TemporalChannel_StoresIsoMinMax_ForDayIntegerStorage()
    {
        // 19723 = 2024-01-01, 19905 = 2024-07-01 as days since epoch, mirroring a ClickHouse Date export.
        string staging = Staging("SELECT x::FLOAT AS x, x::FLOAT AS y, (19723 + (x % 183))::SMALLINT AS day FROM range(400) r(x)");
        var view = View(new ChannelSpec { Name = "day", Column = "day", Role = ChannelRole.Temporal, Type = ChannelType.Date });

        var d = new DuckDbChannelDomainScanner().Scan(staging, view)["day"];
        Assert.Equal(["2024-01-01", "2024-07-01"], d.Values);
    }

    [Fact]
    public void TemporalChannel_StoresIsoMinMax_ForRealDateStorage()
    {
        string staging = Staging("SELECT x::FLOAT AS x, x::FLOAT AS y, DATE '2023-05-05' + INTERVAL (x) DAY AS day FROM range(3) r(x)");
        var view = View(new ChannelSpec { Name = "day", Column = "day", Role = ChannelRole.Temporal, Type = ChannelType.Date });

        var d = new DuckDbChannelDomainScanner().Scan(staging, view)["day"];
        Assert.Equal(["2023-05-05", "2023-05-07"], d.Values);
    }

    [Fact]
    public void MissingColumn_IsOmittedWithoutFailingTheBake()
    {
        string staging = Staging("SELECT x::FLOAT AS x, x::FLOAT AS y FROM range(3) r(x)");
        var view = View(new ChannelSpec { Name = "ghost", Column = "ghost", Role = ChannelRole.Dimension, Type = ChannelType.Dict });

        Assert.Empty(new DuckDbChannelDomainScanner().Scan(staging, view));
    }
}
