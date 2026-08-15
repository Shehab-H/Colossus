using Colossus.Application.Abstractions.Reduction;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Sources;
using Colossus.Infrastructure.Trino;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class TrinoParquetIntegrationTests : IAsyncLifetime
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("colossus-trino-integration-");
    private readonly string _table = $"points_{Guid.NewGuid():N}";
    private readonly string _fixtureDirectory;
    private readonly TrinoClient _client;
    private readonly TrinoConnection _connection;
    private readonly TrinoSourceAdapter _trino;
    private readonly ParquetSourceAdapter _parquet;

    public TrinoParquetIntegrationTests()
    {
        _fixtureDirectory = Path.Combine(RepoPaths.Root, "docker", "trino", "data", _table);
        var clients = new HttpClientFactory();
        var trinoOptions = new TrinoOptions
        {
            BaseUrl = "http://127.0.0.1:8080",
            Catalog = "hive",
            Schema = "colossus",
            User = "colossus",
            WorkDirectory = Path.Combine(_work.FullName, "trino-work"),
            MaxPageBytes = 16 * 1024 * 1024,
            MaxRetries = 3,
        };
        _trino = new TrinoSourceAdapter(Options.Create(trinoOptions), clients);
        _client = new TrinoClient(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });
        _connection = new TrinoConnection(
            new Uri("http://127.0.0.1:8080"), "colossus", "hive", "colossus",
            new Dictionary<string, string>(), new Dictionary<string, string>(), 3, 16 * 1024 * 1024);

        var parquetOptions = new ParquetSourceOptions
        {
            WorkDirectory = Path.Combine(_work.FullName, "parquet-work"),
            Providers = new Dictionary<string, ParquetProviderOptions>
            {
                ["fixture"] = new() { Kind = "local", BaseLocation = Path.Combine(RepoPaths.Root, "docker", "trino", "data") },
            },
        };
        parquetOptions.ResolvePaths();
        _parquet = new ParquetSourceAdapter(Options.Create(parquetOptions), clients);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(RepoPaths.Root, "docker", "trino", "data", "colossus"));
        Directory.CreateDirectory(_fixtureDirectory);
        await WaitUntilReadyAsync(TimeSpan.FromMinutes(3));
        await ExecuteAsync("CREATE SCHEMA IF NOT EXISTS hive.colossus WITH (location = 'file:///data/colossus-fixtures')");
        await ExecuteAsync($"""
            CREATE TABLE hive.colossus.{_table} (
              lon double,
              lat double,
              quadkey varchar,
              wkt varchar,
              category varchar,
              value double
            ) WITH (
              format = 'PARQUET',
              location = 'file:///data/colossus-fixtures/{_table}'
            )
            """);
        await ExecuteAsync($"""
            INSERT INTO hive.colossus.{_table}
            SELECT
              30.0 + number * 0.000001 AS lon,
              29.0 + number * 0.000001 AS lat,
              CASE WHEN number % 2 = 0 THEN '0' ELSE '3' END AS quadkey,
              CASE WHEN number % 2 = 0
                THEN 'POLYGON((0 0,2 0,0 2,0 0))'
                ELSE 'MULTIPOLYGON(((10 10,12 10,10 12,10 10)),((20 20,22 20,20 22,20 20)))'
              END AS wkt,
              CASE WHEN number % 3 = 0 THEN NULL ELSE 'category-' || CAST(number % 5 AS VARCHAR) END AS category,
              CAST(number AS DOUBLE) / 10 AS value
            FROM (
              SELECT base + chunk * 10000 AS number
              FROM UNNEST(sequence(0, 9999)) AS numbers(base)
              CROSS JOIN UNNEST(ARRAY[0, 1]) AS chunks(chunk)
            )
            """);
    }

    public async Task DisposeAsync()
    {
        try { await ExecuteAsync($"DROP TABLE IF EXISTS hive.colossus.{_table}"); }
        finally
        {
            if (Directory.Exists(_fixtureDirectory)) Directory.Delete(_fixtureDirectory, recursive: true);
            _work.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PinnedTrinoReadsParquetStreamsPagesAndMatchesDirectParquetFidelity()
    {
        var trinoDataset = PointDataset($"SELECT lon, lat, category, value FROM hive.colossus.{_table}");
        var parquetDataset = PointDataset($"{_table}/data/**/*.parquet");
        var trinoView = View(trinoDataset.ToSnapshot("trino", ConnectionContext().Settings));
        var parquetView = await _parquet.ResolveAsync(View(parquetDataset.ToSnapshot("parquet", new Dictionary<string, string> { ["provider"] = "fixture" })));

        var validation = await _trino.ValidateConnectionAsync(ConnectionContext());
        var discovery = await _trino.DiscoverAsync(ConnectionContext());
        var preview = await _trino.PreviewAsync(new SourcePreviewRequest(ConnectionContext(), trinoDataset, SourcePreviewBudget.Default));
        var trinoProbe = await _trino.ProbeAsync(trinoView);
        var parquetProbe = await _parquet.ProbeAsync(parquetView);
        string trinoStage = Path.Combine(_work.FullName, "trino.parquet");
        string parquetStage = Path.Combine(_work.FullName, "direct.parquet");
        var progress = new ProgressRecorder();
        await _trino.ExtractAsync(trinoView, trinoProbe.Bounds, trinoStage, progress);
        await _parquet.ExtractAsync(parquetView, parquetProbe.Bounds, parquetStage);

        Assert.Equal("Connection succeeded.", validation.Message);
        Assert.Contains(discovery, location => location.Namespace == "colossus" && location.Name == _table);
        Assert.Equal(50, preview.Rows.Count);
        Assert.True(preview.Truncated);
        Assert.Equal(20_000, trinoProbe.Count);
        Assert.Equal(parquetProbe.Count, trinoProbe.Count);
        Assert.Equal(parquetProbe.Bounds.MinX, trinoProbe.Bounds.MinX, precision: 5);
        Assert.Equal(parquetProbe.Bounds.MaxY, trinoProbe.Bounds.MaxY, precision: 5);
        Assert.True(progress.Events.Count(value => !value.Completed) > 1);
        Assert.True(progress.Events.Last().Completed);
        Assert.Equal(20_000, progress.Events.Last().RowsRead);

        string trinoTiles = Path.Combine(_work.FullName, "trino-tiles");
        string parquetTiles = Path.Combine(_work.FullName, "parquet-tiles");
        var reducer = new RawPassthroughReducer();
        var trinoResult = reducer.Reduce(Context(trinoStage, trinoTiles, trinoView, trinoProbe.Bounds));
        var parquetResult = reducer.Reduce(Context(parquetStage, parquetTiles, parquetView, parquetProbe.Bounds));
        string tile = new TileId(0, 0, 0).RelativePath;
        Assert.Equal(parquetResult.LeafPointCount, trinoResult.LeafPointCount);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(parquetTiles, tile)),
            await File.ReadAllBytesAsync(Path.Combine(trinoTiles, tile)));
    }

    [Fact]
    public async Task RealTrinoQuadkeyAndWktFixturesPassCommonGeometryContract()
    {
        var quadkey = new DatasetSource
        {
            Query = $"SELECT quadkey, category FROM hive.colossus.{_table} ORDER BY lon LIMIT 2",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = [new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict }],
        };
        var quadPreview = await _trino.PreviewAsync(new SourcePreviewRequest(ConnectionContext(), quadkey, SourcePreviewBudget.Default));
        Assert.All(quadPreview.Rows, row => Assert.Equal("[0,5]", row["part_offsets"]));

        var wkt = quadkey with
        {
            Query = $"SELECT wkt, category FROM hive.colossus.{_table} ORDER BY lon LIMIT 2",
            Geometry = new GeometrySpec { Kind = GeometryKind.Wkt, Column = "wkt" },
        };
        var wktPreview = await _trino.PreviewAsync(new SourcePreviewRequest(ConnectionContext(), wkt, SourcePreviewBudget.Default));
        Assert.Equal("[0,4]", wktPreview.Rows[0]["part_offsets"]);
        Assert.Equal("[0,4,8]", wktPreview.Rows[1]["part_offsets"]);
    }

    private async Task WaitUntilReadyAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await ExecuteAsync("SELECT 1");
                await ExecuteAsync("SHOW SCHEMAS FROM hive");
                return;
            }
            catch when (DateTime.UtcNow < deadline) { await Task.Delay(1000); }
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await foreach (var _ in _client.QueryAsync(_connection, sql)) { }
    }

    private static DatasetSource PointDataset(string query) => new()
    {
        Query = query,
        Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
        Channels =
        [
            new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
            new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 },
        ],
    };

    private static SourceConnectionContext ConnectionContext() => new(
        new Dictionary<string, string>
        {
            ["url"] = "http://127.0.0.1:8080",
            ["catalog"] = "hive",
            ["schema"] = "colossus",
            ["user"] = "colossus",
        },
        new Dictionary<string, string>());

    private static ViewConfig View(SourceSpec source) => new()
    {
        Id = "trino-parquet-fidelity",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = source,
    };

    private static ReductionContext Context(string staging, string output, ViewConfig view, Bbox bounds) => new()
    {
        StagingParquetPath = staging,
        OutputDirectory = output,
        Root = bounds,
        TilePointBudget = 50_000,
        MaxZoom = 0,
        View = view,
    };

    private sealed class ProgressRecorder : IProgress<SourceExtractProgress>
    {
        public List<SourceExtractProgress> Events { get; } = [];
        public void Report(SourceExtractProgress value) => Events.Add(value);
    }

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(10) };
    }
}
