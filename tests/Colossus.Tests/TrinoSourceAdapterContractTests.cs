using System.Net;
using System.Text;
using System.Text.Json;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Trino;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class TrinoSourceAdapterContractTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("colossus-trino-source-");
    private readonly TrinoFixtureHandler _handler = new();
    private readonly TrinoSourceAdapter _adapter;

    public TrinoSourceAdapterContractTests()
    {
        var options = new TrinoOptions
        {
            BaseUrl = "http://trino.test:8080",
            Catalog = "hive",
            Schema = "analytics",
            User = "colossus",
            WorkDirectory = Path.Combine(_directory.FullName, "work"),
            MaxPageBytes = 1_048_576,
            MaxRetries = 2,
        };
        _adapter = new TrinoSourceAdapter(Options.Create(options), new HttpClientFactory(_handler));
    }

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task PagedPointQuery_PassesValidationDiscoveryPreviewProbeAndStreamingExtraction()
    {
        var dataset = PointDataset("SELECT lon, lat, category, value, event_date FROM point_fixture");
        var validation = await _adapter.ValidateConnectionAsync(Connection());
        var locations = await _adapter.DiscoverAsync(Connection());
        var preview = await _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), dataset, SourcePreviewBudget.Default));
        var probe = await _adapter.ProbeAsync(View(dataset));
        string destination = Path.Combine(_directory.FullName, "canonical.parquet");
        var progress = new ProgressRecorder();
        await _adapter.ExtractAsync(View(dataset), probe.Bounds, destination, progress);

        Assert.Equal("Connection succeeded.", validation.Message);
        Assert.Equal("483", validation.Details["serverVersion"]);
        Assert.Contains(locations, location => location.Namespace == "analytics" && location.Name == "points");
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("null", preview.Rows[1]["category"]);
        Assert.Equal("2025-01-01", preview.Rows[0]["event_date"]);
        Assert.Contains("connector statistics are estimated", preview.Warnings!);
        Assert.Equal("double", preview.Columns.Single(column => column.Name == "value").SourceType);
        Assert.Equal(2, probe.Count);
        Assert.Equal(2, probe.DistinctGeometries);
        Assert.True(File.Exists(destination));
        Assert.True(progress.Events.Count(value => !value.Completed) >= 2);
        Assert.True(progress.Events.Last().Completed);
        Assert.Equal(2, progress.Events.Last().RowsRead);
        Assert.True(_handler.CompletedPageGets >= 3);

        using var db = DuckDbSession.InMemory();
        using var command = db.Connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{destination.Replace("'", "''")}')";
        using var reader = command.ExecuteReader();
        var schema = new List<(string Name, string Type)>();
        while (reader.Read()) schema.Add((reader.GetString(0), reader.GetString(1)));
        Assert.Equal(["x", "y", "category", "value", "event_date"], schema.Select(column => column.Name));
        Assert.Equal("DOUBLE", schema.Single(column => column.Name == "value").Type);
        Assert.Equal("DATE", schema.Single(column => column.Name == "event_date").Type);
    }

    [Fact]
    public async Task QuadkeyAndMultipartWkt_PassCanonicalGeometryContract()
    {
        var quadkey = new DatasetSource
        {
            Query = "SELECT quadkey, category FROM quadkey_fixture",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = [new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict }],
        };
        var quadPreview = await _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), quadkey, SourcePreviewBudget.Default));
        Assert.Equal("[0,5]", quadPreview.Rows[0]["part_offsets"]);
        Assert.Equal(10, JsonSerializer.Deserialize<float[]>(quadPreview.Rows[0]["geometry"]!)!.Length);

        var wkt = quadkey with
        {
            Query = "SELECT wkt, category FROM wkt_fixture",
            Geometry = new GeometrySpec { Kind = GeometryKind.Wkt, Column = "wkt" },
        };
        var wktPreview = await _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), wkt, SourcePreviewBudget.Default));
        Assert.Equal("[0,4,8]", wktPreview.Rows[0]["part_offsets"]);
        Assert.Equal(16, JsonSerializer.Deserialize<float[]>(wktPreview.Rows[0]["geometry"]!)!.Length);
    }

    [Fact]
    public async Task TypeOverflow_IdentifiesChannelAndTrinoSourceType()
    {
        var dataset = PointDataset("SELECT lon, lat, category, value, event_date FROM overflow_fixture") with
        {
            Channels = [new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.U8 }],
        };

        var error = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.PreviewAsync(
            new SourcePreviewRequest(Connection(), dataset, SourcePreviewBudget.Default)));

        Assert.Equal(SourceErrorKind.Type, error.Kind);
        Assert.Contains("source.channels.value", error.FieldErrors.Keys);
        Assert.Contains("bigint", error.Message);
    }

    [Fact]
    public async Task PreviewEnforcesScanCeilingAndRejectsMutatingOrMultipleStatements()
    {
        var scan = PointDataset("SELECT lon, lat, category, value, event_date FROM large_fixture");
        var scanError = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.PreviewAsync(
            new SourcePreviewRequest(Connection(), scan, new SourcePreviewBudget(50, 1_048_576, 100, TimeSpan.FromSeconds(10)))));
        Assert.Equal(SourceErrorKind.TransientFailure, scanError.Kind);
        Assert.Contains("preview", scanError.FieldErrors.Keys);
        Assert.True(_handler.Deletes > 0);

        foreach (string query in new[] { "DELETE FROM point_fixture", "SELECT * FROM point_fixture; DROP TABLE point_fixture" })
        {
            var error = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.PreviewAsync(
                new SourcePreviewRequest(Connection(), PointDataset(query), SourcePreviewBudget.Default)));
            Assert.Equal(SourceErrorKind.Configuration, error.Kind);
            Assert.Contains("source.query", error.FieldErrors.Keys);
        }
    }

    [Fact]
    public async Task CancellationDeletesTrinoQueryAndLeavesNoPublishablePartialFile()
    {
        var dataset = PointDataset("SELECT lon, lat, category, value, event_date FROM slow_fixture");
        string destination = Path.Combine(_directory.FullName, "cancelled.parquet");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _adapter.ExtractAsync(View(dataset), new Bbox(0, 0, 1, 1), destination, ct: cancellation.Token));

        Assert.True(_handler.Deletes > 0);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.partial", SearchOption.AllDirectories));
    }

    private static SourceConnectionContext Connection() => new(
        new Dictionary<string, string>
        {
            ["url"] = "http://trino.test:8080",
            ["catalog"] = "hive",
            ["schema"] = "analytics",
            ["user"] = "colossus",
            ["sessionProperties"] = "{\"query_max_execution_time\":\"10m\"}",
        },
        new Dictionary<string, string>());

    private static DatasetSource PointDataset(string query) => new()
    {
        Query = query,
        Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
        Channels =
        [
            new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
            new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 },
            new ChannelSpec { Name = "event_date", Column = "event_date", Role = ChannelRole.Temporal, Type = ChannelType.Date },
        ],
    };

    private static ViewConfig View(DatasetSource dataset) => new()
    {
        Id = "trino-contract",
        Viewport = Viewport.Geo,
        Mark = dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt ? Mark.Polygon : Mark.Point,
        Source = dataset.ToSnapshot("trino", Connection().Settings),
    };

    private sealed class ProgressRecorder : IProgress<SourceExtractProgress>
    {
        public List<SourceExtractProgress> Events { get; } = [];
        public void Report(SourceExtractProgress value) => Events.Add(value);
    }

    private sealed class HttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private sealed class TrinoFixtureHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _queries = new(StringComparer.Ordinal);
        private int _querySequence;
        public int Deletes { get; private set; }
        public int CompletedPageGets { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get)
            {
                string id = request.RequestUri!.Segments[^2].Trim('/');
                string query = _queries[id];
                if (query.Contains("slow_fixture", StringComparison.Ordinal))
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                CompletedPageGets++;
                return Page(id, query, final: true);
            }

            string sql = await request.Content!.ReadAsStringAsync(ct);
            if (sql.Contains("version()", StringComparison.OrdinalIgnoreCase))
                return Json(new
                {
                    id = "validate",
                    columns = Columns(("_col0", "varchar"), ("current_catalog", "varchar"), ("current_schema", "varchar")),
                    data = new[] { new object?[] { "483", "hive", "analytics" } },
                    stats = Stats("FINISHED", 1),
                });
            if (sql.Contains("information_schema.tables", StringComparison.OrdinalIgnoreCase))
                return Json(new
                {
                    id = "discover",
                    columns = Columns(("table_schema", "varchar"), ("table_name", "varchar"), ("table_type", "varchar")),
                    data = new[] { new object?[] { "analytics", "points", "BASE TABLE" } },
                    stats = Stats("FINISHED", 1),
                });

            string queryId = $"query-{Interlocked.Increment(ref _querySequence)}";
            _queries[queryId] = sql;
            return Page(queryId, sql, final: false);
        }

        private HttpResponseMessage Page(string id, string query, bool final)
        {
            if (query.Contains("slow_fixture", StringComparison.Ordinal))
                return Json(new { id, nextUri = Next(id), stats = Stats("RUNNING", 0) });

            var fixture = Fixture(query, final);
            long processedRows = query.Contains("large_fixture", StringComparison.Ordinal) ? 2_000_000 : final ? 2 : 1;
            return Json(new
            {
                id,
                nextUri = final ? null : Next(id),
                columns = final ? null : fixture.Columns,
                data = fixture.Data,
                stats = Stats(final ? "FINISHED" : "RUNNING", processedRows),
                warnings = final ? null : new[] { new { message = "connector statistics are estimated" } },
            });
        }

        private static FixturePage Fixture(string query, bool final)
        {
            if (query.Contains("quadkey_fixture", StringComparison.Ordinal))
                return new FixturePage(Columns(("quadkey", "varchar"), ("category", "varchar")), [final ? ["3", "b"] : ["0", "a"]]);
            if (query.Contains("wkt_fixture", StringComparison.Ordinal))
                return new FixturePage(
                    Columns(("wkt", "varchar"), ("category", "varchar")),
                    [final
                        ? ["POLYGON((20 20,22 20,20 22,20 20),(20.2 20.2,20.4 20.2,20.2 20.4,20.2 20.2))", null]
                        : ["MULTIPOLYGON(((0 0,2 0,0 2,0 0)),((10 10,12 10,10 12,10 10)))", "a"]]);
            if (query.Contains("overflow_fixture", StringComparison.Ordinal))
                return new FixturePage(Columns(("lon", "double"), ("lat", "double"), ("value", "bigint")), final ? [] : [[31.2, 30.0, 999L]]);

            object?[] first = [31.2, 30.0, "urban", 12.5, "2025-01-01"];
            object?[] second = [31.3, 30.1, null, 9.5, "2025-01-02"];
            return new FixturePage(
                Columns(("lon", "double"), ("lat", "double"), ("category", "varchar"), ("value", "double"), ("event_date", "date")),
                [final ? second : first]);
        }

        private static object[] Columns(params (string Name, string Type)[] values) =>
            values.Select(value => (object)new { name = value.Name, type = value.Type }).ToArray();
        private static object Stats(string state, long rows) => new { state, processedRows = rows, processedBytes = rows * 64 };
        private static string Next(string id) => $"http://trino.test:8080/v1/statement/{id}/1";
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

        private sealed record FixturePage(object[] Columns, object?[][] Data);
    }
}
