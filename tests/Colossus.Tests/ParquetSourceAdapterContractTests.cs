using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Sources;
using Microsoft.Extensions.Options;
using Xunit;

namespace Colossus.Tests;

public sealed class ParquetSourceAdapterContractTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("colossus-parquet-source-");
    private readonly string _root;
    private readonly ParquetSourceAdapter _adapter;

    public ParquetSourceAdapterContractTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_directory.FullName, "provider")).FullName;
        var options = new ParquetSourceOptions
        {
            WorkDirectory = Path.Combine(_directory.FullName, "work"),
            Providers = new Dictionary<string, ParquetProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixture"] = new() { Kind = "local", BaseLocation = _root },
            },
        };
        options.ResolvePaths();
        _adapter = new ParquetSourceAdapter(Options.Create(options), new HttpClientFactory());
    }

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public async Task LocalPartitionedInput_PassesDiscoveryPreviewProbeExtractAndSnapshotContract()
    {
        WritePointPartitions();
        var connection = Connection();
        var dataset = PointDataset();

        var validation = await _adapter.ValidateConnectionAsync(connection);
        var locations = await _adapter.DiscoverAsync(connection);
        var preview = await _adapter.PreviewAsync(new SourcePreviewRequest(connection, dataset, SourcePreviewBudget.Default));
        var resolved = await _adapter.ResolveAsync(View(dataset));
        var probe = await _adapter.ProbeAsync(resolved);
        string destination = Path.Combine(_directory.FullName, "canonical.parquet");
        var progress = new ProgressRecorder();
        await _adapter.ExtractAsync(resolved, probe.Bounds, destination, progress);

        Assert.Equal("Parquet provider is ready.", validation.Message);
        Assert.Equal(2, locations.Count);
        Assert.All(locations, location => Assert.Equal("Parquet file", location.Kind));
        Assert.Equal(3, preview.Rows.Count);
        Assert.Equal("null", preview.Rows.Single(row => row["name"] == "null")["name"]);
        Assert.Equal(31.2, preview.Geometry.Bounds.MinX, precision: 4);
        Assert.Equal(30, preview.Geometry.Bounds.MinY, precision: 4);
        Assert.Equal(31.4, preview.Geometry.Bounds.MaxX, precision: 4);
        Assert.Equal(30.2, preview.Geometry.Bounds.MaxY, precision: 4);
        Assert.Equal(3, probe.Count);
        Assert.Equal(3, probe.DistinctGeometries);
        Assert.Equal(2, resolved.Source.InputSnapshot!.Inputs.Count);
        Assert.StartsWith("sha256:", resolved.Source.InputSnapshot.Fingerprint);
        Assert.True(File.Exists(destination));
        Assert.True(progress.Events.Single().Completed);

        using var duckDb = DuckDbSession.InMemory();
        using var command = duckDb.Connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{Sql.Path(destination)}')";
        using var reader = command.ExecuteReader();
        var schema = new List<(string Name, string Type)>();
        while (reader.Read()) schema.Add((reader.GetString(0), reader.GetString(1)));
        Assert.Equal(["x", "y", "name", "value"], schema.Select(column => column.Name));
        Assert.Equal("FLOAT", schema[0].Type);
        Assert.Equal("DOUBLE", schema[3].Type);
    }

    [Fact]
    public async Task QuadkeyAndMultipartWkt_UseCanonicalGeometrySemantics()
    {
        string file = Path.Combine(_root, "geometry.parquet");
        Write(file, """
            SELECT * FROM (VALUES
              ('0', 'MULTIPOLYGON(((0 0,2 0,0 2,0 0)),((10 10,12 10,10 12,10 10)))', 'a'),
              ('3', 'POLYGON((20 20,22 20,20 22,20 20),(20.2 20.2,20.4 20.2,20.2 20.4,20.2 20.2))', NULL)
            ) v(quadkey, wkt, category)
            """);

        var quadkey = new DatasetSource
        {
            Query = "geometry.parquet",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = [new ChannelSpec { Name = "category", Column = "category", Role = ChannelRole.Dimension, Type = ChannelType.Dict }],
        };
        var quadPreview = await _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), quadkey, SourcePreviewBudget.Default));
        Assert.Equal("[0,5]", quadPreview.Rows[0]["part_offsets"]);
        Assert.Equal(10, System.Text.Json.JsonSerializer.Deserialize<float[]>(quadPreview.Rows[0]["geometry"]!)!.Length);

        var wkt = quadkey with { Geometry = new GeometrySpec { Kind = GeometryKind.Wkt, Column = "wkt" } };
        var wktPreview = await _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), wkt, SourcePreviewBudget.Default));
        Assert.Equal("[0,4,8]", wktPreview.Rows[0]["part_offsets"]);
        Assert.Equal("[0,4,8]", wktPreview.Rows[1]["part_offsets"]);
        Assert.Equal(16, System.Text.Json.JsonSerializer.Deserialize<float[]>(wktPreview.Rows[0]["geometry"]!)!.Length);
        Assert.Equal("null", wktPreview.Rows[1]["category"]);
    }

    [Fact]
    public async Task SnapshotDetectsChangedFilesAndLocationsCannotEscapeProviderRoot()
    {
        WritePointPartitions();
        var resolved = await _adapter.ResolveAsync(View(PointDataset()));
        string changed = Path.Combine(_root, "points", "year=2025", "part-a.parquet");
        Write(changed, "SELECT 31.2::DOUBLE AS lon, 30::DOUBLE AS lat, 'urban'::VARCHAR AS \"name\", '12.5'::VARCHAR AS \"value\" UNION ALL SELECT 31.25, 30.05, 'new', '13'");

        var changedError = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.ProbeAsync(resolved));
        Assert.Contains("changed", changedError.Message, StringComparison.OrdinalIgnoreCase);

        var escape = PointDataset() with { Query = "../outside.parquet" };
        var escapeError = await Assert.ThrowsAsync<SourceOperationException>(() =>
            _adapter.PreviewAsync(new SourcePreviewRequest(Connection(), escape, SourcePreviewBudget.Default)));
        Assert.Equal(SourceErrorKind.Configuration, escapeError.Kind);
        Assert.Contains("source.query", escapeError.FieldErrors.Keys);
    }

    [Fact]
    public void DuckDbPlan_ProjectsOnlyMappedColumnsFromParquet()
    {
        WritePointPartitions();
        string path = Path.Combine(_root, "points", "**", "*.parquet").Replace('\\', '/');
        using var db = DuckDbSession.InMemory();
        using var command = db.Connection.CreateCommand();
        command.CommandText = $"EXPLAIN SELECT lon, lat, name, value FROM read_parquet('{path}') WHERE value = '12.5'";
        using var reader = command.ExecuteReader();
        var plan = new List<string>();
        while (reader.Read()) plan.Add(string.Join(" ", Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)));
        string text = string.Join("\n", plan);
        Assert.Contains("READ_PARQUET", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lon", text);
        Assert.Contains("Filters", text);
        Assert.DoesNotContain("unused_payload", text);
    }

    [Fact]
    public async Task CancellationInterruptsAnActiveParquetProbe()
    {
        string file = Path.Combine(_root, "large.parquet");
        Write(file, "SELECT number::DOUBLE AS lon, (number % 80)::DOUBLE AS lat, number::VARCHAR AS \"value\" FROM range(2000000) t(number)");
        var dataset = new DatasetSource
        {
            Query = "large.parquet",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            Channels = [new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 }],
        };
        var budgetError = await Assert.ThrowsAsync<SourceOperationException>(() => _adapter.PreviewAsync(
            new SourcePreviewRequest(Connection(), dataset, new SourcePreviewBudget(50, 1_048_576, 1_000, TimeSpan.FromSeconds(10)))));
        Assert.Equal(SourceErrorKind.TransientFailure, budgetError.Kind);
        Assert.Contains("preview", budgetError.FieldErrors.Keys);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _adapter.ProbeAsync(View(dataset), cancellation.Token));
    }

    [Fact]
    public async Task HttpRangeInput_PassesBoundedPreviewContract()
    {
        string file = Path.Combine(_directory.FullName, "remote.parquet");
        Write(file, "SELECT 31.2::DOUBLE AS lon, 30::DOUBLE AS lat, 12.5::DOUBLE AS speed");
        using var server = new RangeFileServer(file);
        var options = new ParquetSourceOptions
        {
            WorkDirectory = Path.Combine(_directory.FullName, "http-work"),
            Providers = new Dictionary<string, ParquetProviderOptions>
            {
                ["public"] = new() { Kind = "http", BaseLocation = server.BaseUrl },
            },
        };
        options.ResolvePaths();
        var adapter = new ParquetSourceAdapter(Options.Create(options), new HttpClientFactory());
        var dataset = new DatasetSource
        {
            Query = "remote.parquet",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            Channels = [new ChannelSpec { Name = "speed", Column = "speed", Role = ChannelRole.Measure, Type = ChannelType.F64 }],
        };

        var preview = await adapter.PreviewAsync(new SourcePreviewRequest(
            new SourceConnectionContext(new Dictionary<string, string> { ["provider"] = "public" }, new Dictionary<string, string>()),
            dataset,
            SourcePreviewBudget.Default));

        Assert.Single(preview.Rows);
        Assert.Equal("12.5", preview.Rows[0]["speed"]);
        Assert.True(server.RangeRequests > 0);
    }

    private void WritePointPartitions()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "points", "year=2025")).FullName;
        Write(Path.Combine(directory, "part-a.parquet"), """
            SELECT * FROM (VALUES
              (31.2::DOUBLE, 30.0::DOUBLE, 'urban'::VARCHAR, '12.5'::VARCHAR, repeat('x', 10000)),
              (31.3::DOUBLE, 30.1::DOUBLE, NULL::VARCHAR, '14.0'::VARCHAR, repeat('y', 10000))
            ) v(lon, lat, "name", "value", unused_payload)
            """);
        Write(Path.Combine(directory, "part-b.parquet"), """
            SELECT 31.4::DOUBLE AS lon, 30.2::DOUBLE AS lat, 'remote'::VARCHAR AS "name",
                   '9.5'::VARCHAR AS "value", repeat('z', 10000) AS unused_payload
            """);
    }

    private static void Write(string path, string query)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = DuckDbSession.InMemory();
        db.Exec($"COPY ({query}) TO '{Sql.Path(path)}' (FORMAT PARQUET)");
    }

    private static SourceConnectionContext Connection() => new(
        new Dictionary<string, string> { ["provider"] = "fixture" },
        new Dictionary<string, string>());

    private static DatasetSource PointDataset() => new()
    {
        Query = "points/**/*.parquet",
        Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
        Channels =
        [
            new ChannelSpec { Name = "name", Column = "name", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
            new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F64 },
        ],
    };

    private static ViewConfig View(DatasetSource dataset) => new()
    {
        Id = "parquet-contract",
        Viewport = Viewport.Geo,
        Mark = dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt ? Mark.Polygon : Mark.Point,
        Source = dataset.ToSnapshot("parquet", new Dictionary<string, string> { ["provider"] = "fixture" }),
    };

    private sealed class HttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromMinutes(5) };
    }

    private sealed class ProgressRecorder : IProgress<SourceExtractProgress>
    {
        public List<SourceExtractProgress> Events { get; } = [];
        public void Report(SourceExtractProgress value) => Events.Add(value);
    }

    private sealed class RangeFileServer : IDisposable
    {
        private readonly string _file;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _rangeRequests;

        public RangeFileServer(string file)
        {
            _file = file;
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _loop = ServeAsync();
        }

        public string BaseUrl { get; }
        public int RangeRequests => Volatile.Read(ref _rangeRequests);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _loop.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) when (_stop.IsCancellationRequested) { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (NetworkStream stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            {
                string? requestLine = await reader.ReadLineAsync(_stop.Token);
                if (requestLine is null) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    int separator = line.IndexOf(':');
                    if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }

                byte[] content = await File.ReadAllBytesAsync(_file, _stop.Token);
                bool head = requestLine.StartsWith("HEAD ", StringComparison.Ordinal);
                long start = 0, end = content.LongLength - 1;
                bool partial = false;
                if (headers.TryGetValue("Range", out string? range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = range[6..].Split('-', 2);
                    start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                    if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
                        end = Math.Min(end, long.Parse(parts[1], CultureInfo.InvariantCulture));
                    partial = true;
                    Interlocked.Increment(ref _rangeRequests);
                }
                long length = Math.Max(0, end - start + 1);
                string status = partial ? "206 Partial Content" : "200 OK";
                var response = new StringBuilder()
                    .Append("HTTP/1.1 ").Append(status).Append("\r\n")
                    .Append("Content-Type: application/octet-stream\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("ETag: \"fixture-v1\"\r\n")
                    .Append("Content-Length: ").Append(length).Append("\r\n");
                if (partial) response.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(content.LongLength).Append("\r\n");
                response.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response.ToString()), _stop.Token);
                if (!head && length > 0) await stream.WriteAsync(content.AsMemory((int)start, (int)length), _stop.Token);
            }
        }
    }
}
