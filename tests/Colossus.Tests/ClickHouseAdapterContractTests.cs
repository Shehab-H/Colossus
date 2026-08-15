using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class ClickHouseAdapterContractTests : IAsyncLifetime
{
    private readonly string _table = $"colossus.preview_contract_{Guid.NewGuid():N}";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("colossus-source-contract-");
    private readonly ClickHouseClient _client;
    private readonly ClickHouseAdapter _adapter;

    public ClickHouseAdapterContractTests()
    {
        var options = new ClickHouseOptions
        {
            BaseUrl = "http://127.0.0.1:8123",
            User = "colossus",
            Password = "colossus",
        };
        _client = new ClickHouseClient(options);
        _adapter = new ClickHouseAdapter(_client, new HttpClientFactory(), Options.Create(options));
    }

    public async Task InitializeAsync()
    {
        await _client.WaitUntilReadyAsync(TimeSpan.FromSeconds(20));
        await _client.ExecuteAsync($"""
            CREATE TABLE {_table}
            (
                lon Float64,
                lat Float64,
                category Nullable(String),
                value Float64
            )
            ENGINE = Memory
            """);
        await _client.ExecuteAsync($"""
            INSERT INTO {_table} VALUES
              (31.20, 30.00, 'urban', 12.5),
              (31.30, 30.10, NULL, 14.0),
              (120.00, 30.20, 'remote', 9.5)
            """);
    }

    public async Task DisposeAsync()
    {
        await _client.ExecuteAsync($"DROP TABLE IF EXISTS {_table}");
        _client.Dispose();
        _directory.Delete(recursive: true);
    }

    [Fact]
    public async Task ClickHouse_PassesConnectionDiscoveryPreviewProbeAndExtractionContract()
    {
        var connection = new SourceConnectionContext(
            new Dictionary<string, string>(), new Dictionary<string, string>());
        var validation = await _adapter.ValidateConnectionAsync(connection);
        var locations = await _adapter.DiscoverAsync(connection);
        var dataset = Dataset();

        var preview = await _adapter.PreviewAsync(new SourcePreviewRequest(
            connection, dataset, SourcePreviewBudget.Default));
        var view = View(dataset);
        var probe = await _adapter.ProbeAsync(view);
        string parquet = Path.Combine(_directory.FullName, "fixture.parquet");
        var progress = new ExtractProgressRecorder();
        await _adapter.ExtractAsync(view, probe.Bounds, parquet, progress);

        Assert.Equal("Connection succeeded.", validation.Message);
        Assert.Contains(locations, location => $"{location.Namespace}.{location.Name}" == _table);
        Assert.Equal(3, preview.Rows.Count);
        Assert.Equal(new Bbox(31.2, 30, 120, 30.2), preview.Geometry.Bounds);
        Assert.Equal(3, preview.Geometry.DistinctGeometries);
        Assert.Equal(3, probe.Count);
        Assert.Equal(preview.Geometry.Bounds, probe.Bounds);
        Assert.True(File.Exists(parquet));
        Assert.True(new FileInfo(parquet).Length > 0);
        Assert.True(progress.Events.Last().Completed);

        using var duckDb = DuckDbSession.InMemory();
        using var command = duckDb.Connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{Sql.Path(parquet).Replace("'", "''")}')";
        using var reader = command.ExecuteReader();
        var extractedColumns = new List<string>();
        while (reader.Read()) extractedColumns.Add(reader.GetString(0));
        Assert.Equal(preview.Columns.Select(column => column.Name), extractedColumns);
    }

    [Fact]
    public async Task ClickHouse_PreviewRejectsSwappedGeographicCoordinatesWithFieldError()
    {
        var dataset = Dataset() with
        {
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lat", Lat = "lon" },
        };

        var error = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.PreviewAsync(
            new SourcePreviewRequest(
                new SourceConnectionContext(new Dictionary<string, string>(), new Dictionary<string, string>()),
                dataset,
                SourcePreviewBudget.Default)));

        Assert.Equal(SourceErrorKind.Type, error.Kind);
        Assert.Contains("swapped", error.FieldErrors["source.geometry"].Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClickHouse_PreviewCancellationStopsTheSourceRequest()
    {
        var dataset = Dataset() with
        {
            Query = "SELECT number AS lon, sleepEachRow(0.01) + number AS lat, 'x' AS category, number AS value FROM numbers(1000)",
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _adapter.PreviewAsync(
            new SourcePreviewRequest(
                new SourceConnectionContext(new Dictionary<string, string>(), new Dictionary<string, string>()),
                dataset,
                SourcePreviewBudget.Default),
            cancellation.Token));
    }

    private DatasetSource Dataset() => new()
    {
        Query = $"SELECT lon, lat, category, value FROM {_table} ORDER BY lon",
        Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
        Channels =
        [
            new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
            new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 },
        ],
    };

    private static ViewConfig View(DatasetSource dataset) => new()
    {
        Id = "source-contract",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = dataset.ToSnapshot("clickhouse"),
    };

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(30) };
    }

    private sealed class ExtractProgressRecorder : IProgress<SourceExtractProgress>
    {
        public List<SourceExtractProgress> Events { get; } = [];
        public void Report(SourceExtractProgress value) => Events.Add(value);
    }
}
