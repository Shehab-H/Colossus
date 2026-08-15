using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Application.Abstractions.Sources;
using Colossus.Infrastructure;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class ParquetClickHouseFidelityTests : IAsyncLifetime
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("colossus-parquet-fidelity-");
    private readonly string _table = $"colossus.parquet_fidelity_{Guid.NewGuid():N}";
    private readonly ClickHouseClient _clickHouse;
    private readonly ClickHouseAdapter _clickHouseAdapter;
    private readonly ParquetSourceAdapter _parquetAdapter;

    public ParquetClickHouseFidelityTests()
    {
        var clickHouseOptions = new ClickHouseOptions
        {
            BaseUrl = "http://127.0.0.1:8123",
            User = "colossus",
            Password = "colossus",
        };
        var clients = new HttpClientFactory();
        _clickHouse = new ClickHouseClient(clickHouseOptions);
        _clickHouseAdapter = new ClickHouseAdapter(_clickHouse, clients, Options.Create(clickHouseOptions));

        string providerRoot = Directory.CreateDirectory(Path.Combine(_directory.FullName, "provider")).FullName;
        var parquetOptions = new ParquetSourceOptions
        {
            WorkDirectory = Path.Combine(_directory.FullName, "work"),
            Providers = new Dictionary<string, ParquetProviderOptions>
            {
                ["fixture"] = new() { Kind = "local", BaseLocation = providerRoot },
            },
        };
        parquetOptions.ResolvePaths();
        _parquetAdapter = new ParquetSourceAdapter(Options.Create(parquetOptions), clients);
        using var db = DuckDbSession.InMemory();
        db.Exec($"""
            COPY (SELECT * FROM (VALUES
              (31.20::DOUBLE, 30.00::DOUBLE, 12.5::DOUBLE),
              (31.30::DOUBLE, 30.10::DOUBLE, 14.0::DOUBLE),
              (31.40::DOUBLE, 30.20::DOUBLE, 9.5::DOUBLE)
            ) v(lon, lat, value))
            TO '{Sql.Path(Path.Combine(providerRoot, "fixture.parquet"))}' (FORMAT PARQUET)
            """);
    }

    public async Task InitializeAsync()
    {
        await _clickHouse.WaitUntilReadyAsync(TimeSpan.FromSeconds(20));
        await _clickHouse.ExecuteAsync($"CREATE TABLE {_table} (lon Float64, lat Float64, value Float64) ENGINE = Memory");
        await _clickHouse.ExecuteAsync($"INSERT INTO {_table} VALUES (31.2, 30, 12.5), (31.3, 30.1, 14), (31.4, 30.2, 9.5)");
    }

    public async Task DisposeAsync()
    {
        await _clickHouse.ExecuteAsync($"DROP TABLE IF EXISTS {_table}");
        _clickHouse.Dispose();
        _directory.Delete(recursive: true);
    }

    [Fact]
    public async Task RepresentativeDataset_ProducesFidelityEquivalentRawTiles()
    {
        var mapping = new DatasetSource
        {
            Query = "fixture.parquet",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            Channels = [new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 }],
        };
        var parquetView = View(mapping.ToSnapshot("parquet", new Dictionary<string, string> { ["provider"] = "fixture" }));
        parquetView = await _parquetAdapter.ResolveAsync(parquetView);
        var clickHouseView = View((mapping with { Query = $"SELECT lon, lat, value FROM {_table}" }).ToSnapshot("clickhouse"));

        var parquetProbe = await _parquetAdapter.ProbeAsync(parquetView);
        var clickHouseProbe = await _clickHouseAdapter.ProbeAsync(clickHouseView);
        string parquetStage = Path.Combine(_directory.FullName, "parquet-stage.parquet");
        string clickHouseStage = Path.Combine(_directory.FullName, "clickhouse-stage.parquet");
        await _parquetAdapter.ExtractAsync(parquetView, parquetProbe.Bounds, parquetStage);
        await _clickHouseAdapter.ExtractAsync(clickHouseView, clickHouseProbe.Bounds, clickHouseStage);

        string parquetTiles = Path.Combine(_directory.FullName, "parquet-tiles");
        string clickHouseTiles = Path.Combine(_directory.FullName, "clickhouse-tiles");
        var reducer = new RawPassthroughReducer();
        var parquetResult = reducer.Reduce(Context(parquetStage, parquetTiles, parquetView, parquetProbe.Bounds));
        var clickHouseResult = reducer.Reduce(Context(clickHouseStage, clickHouseTiles, clickHouseView, clickHouseProbe.Bounds));
        string tile = new TileId(0, 0, 0).RelativePath;

        Assert.Equal(clickHouseProbe.Count, parquetProbe.Count);
        Assert.Equal(clickHouseProbe.DistinctGeometries, parquetProbe.DistinctGeometries);
        Assert.Equal(clickHouseProbe.Bounds.MinX, parquetProbe.Bounds.MinX, precision: 4);
        Assert.Equal(clickHouseProbe.Bounds.MinY, parquetProbe.Bounds.MinY, precision: 4);
        Assert.Equal(clickHouseProbe.Bounds.MaxX, parquetProbe.Bounds.MaxX, precision: 4);
        Assert.Equal(clickHouseProbe.Bounds.MaxY, parquetProbe.Bounds.MaxY, precision: 4);
        Assert.Equal(clickHouseResult.LeafPointCount, parquetResult.LeafPointCount);
        Assert.Equal(clickHouseResult.Tiles.ToArray(), parquetResult.Tiles.ToArray());
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(clickHouseTiles, tile)),
            await File.ReadAllBytesAsync(Path.Combine(parquetTiles, tile)));
    }

    private static ViewConfig View(SourceSpec source) => new()
    {
        Id = "source-fidelity",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = source,
    };

    private static ReductionContext Context(
        string staging,
        string output,
        ViewConfig view,
        Bbox bounds) => new()
    {
        StagingParquetPath = staging,
        OutputDirectory = output,
        Root = bounds,
        TilePointBudget = 100,
        MaxZoom = 0,
        View = view,
    };

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(5) };
    }
}
