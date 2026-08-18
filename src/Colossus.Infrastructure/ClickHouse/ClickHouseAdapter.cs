using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.ClickHouse.Geometry;
using Colossus.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.ClickHouse;

/// <summary>Normalizes ClickHouse queries into Colossus's canonical spatial schema for bounded preview
/// and full extraction. Connection-specific clients are created through the HTTP client factory while
/// legacy bakes continue to use the configured default connection.</summary>
public sealed class ClickHouseAdapter(
    ClickHouseClient defaultClient,
    IHttpClientFactory httpClients,
    IOptions<ClickHouseOptions> defaultOptions,
    BakeResourceOptions? resources = null) : ISourceAdapter
{
    private readonly BakeResourceOptions _resources = resources ?? new BakeResourceOptions();

    public SourceAdapterDescriptor Descriptor { get; } = new(
        Id: "clickhouse",
        Name: "ClickHouse",
        Capabilities: new SourceAdapterCapabilities(
            ConnectionValidation: true,
            LocationDiscovery: true,
            SchemaInspection: true,
            BoundedPreview: true,
            CanonicalExtraction: true),
        ConfigurationFields:
        [
            new SourceConfigurationField("url", "HTTP URL", Required: true, Placeholder: "http://localhost:8123"),
            new SourceConfigurationField("user", "User", Required: false, Placeholder: "colossus"),
            new SourceConfigurationField("password", "Password", Required: false, Secret: true),
        ],
        GeometryKinds: [GeometryKind.Xy, GeometryKind.LonLat, GeometryKind.Quadkey, GeometryKind.Wkt]);

    public Task<SourceConnectionValidation> ValidateConnectionAsync(
        SourceConnectionContext connection, CancellationToken ct = default) =>
        WithClientAsync(connection, async (client, token) =>
        {
            string[] fields = (await client.QueryTextAsync(
                "SELECT version(), currentDatabase() FORMAT TabSeparated", maxBytes: 64 * 1024, token))
                .Trim().Split('\t');
            return new SourceConnectionValidation(
                "Connection succeeded.",
                new Dictionary<string, string>
                {
                    ["serverVersion"] = fields.ElementAtOrDefault(0) ?? "unknown",
                    ["database"] = fields.ElementAtOrDefault(1) ?? "default",
                });
        }, ct);

    public Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
        SourceConnectionContext connection, CancellationToken ct = default) =>
        WithClientAsync<IReadOnlyList<SourceLocation>>(connection, async (client, token) =>
        {
            const string query = """
                SELECT database, name, engine
                FROM system.tables
                WHERE database NOT IN ('system', 'information_schema', 'INFORMATION_SCHEMA')
                ORDER BY database, name
                LIMIT 200
                FORMAT JSONEachRow
                """;
            string response = await client.QueryTextAsync(query, maxBytes: 512 * 1024, token);
            return response.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    return new SourceLocation(
                        root.GetProperty("database").GetString() ?? "default",
                        root.GetProperty("name").GetString() ?? "unknown",
                        root.GetProperty("engine").GetString() ?? "table");
                })
                .ToArray();
        }, ct);

    public Task<SourceSchema> InspectSchemaAsync(
        SourceSchemaRequest request,
        CancellationToken ct = default) =>
        WithClientAsync(request.Connection, async (client, token) =>
        {
            string query = request.Query.Trim().TrimEnd(';');
            string response = await client.QueryTextAsync($"""
                SELECT *
                FROM (
                {query}
                ) AS colossus_source
                LIMIT 0
                FORMAT JSON
                """, maxBytes: 1 * 1024 * 1024, token);
            using var document = JsonDocument.Parse(response);
            var columns = document.RootElement.GetProperty("meta").EnumerateArray()
                .Select(column =>
                {
                    string name = column.GetProperty("name").GetString() ?? "unknown";
                    string type = column.GetProperty("type").GetString() ?? "Unknown";
                    return new SourceSchemaColumn(
                        name,
                        type,
                        type.StartsWith("Nullable(", StringComparison.Ordinal));
                })
                .ToArray();
            return new SourceSchema(columns);
        }, ct, SourcePreviewBudget.Default.MaxDuration, "query");

    public Task<SourcePreview> PreviewAsync(SourcePreviewRequest request, CancellationToken ct = default)
    {
        var budget = request.Budget.ConstrainTo(SourcePreviewBudget.Default);
        return WithClientAsync(request.Connection, async (client, token) =>
        {
            var timer = Stopwatch.StartNew();
            string sql = PreviewQuery(request.Dataset, budget);
            string response = await client.QueryTextAsync(sql, budget.MaxResultBytes + 64 * 1024, token);
            var preview = ParsePreview(response, request.Dataset, budget, timer.Elapsed);
            return preview with { Elapsed = timer.Elapsed };
        }, ct, budget.MaxDuration, "preview");
    }

    public async Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default)
    {
        try
        {
            // Distinct geometry count only selects the reduction strategy; it is not a fidelity witness.
            // The bounded-memory sketch prevents probing the original source from building an unbounded
            // uniqExact hash set before streaming extraction has even started.
            string sql = $"SELECT min({TileSchema.X}), max({TileSchema.X}), min({TileSchema.Y}), max({TileSchema.Y}), " +
                         $"count(), uniqCombined64(({TileSchema.X}, {TileSchema.Y})) FROM (\n{Bounds(view)}\n)" +
                         ResourceSettings() + "\nFORMAT TabSeparated";
            string[] fields = (await defaultClient.QueryTextAsync(sql, ct)).Trim().Split('\t');
            double D(int index) => double.Parse(fields[index], CultureInfo.InvariantCulture);
            long L(int index) => long.Parse(fields[index], CultureInfo.InvariantCulture);
            return new SourceBounds(new Bbox(D(0), D(2), D(1), D(3)), L(4), L(5));
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw Classify(error, ct, "source.query");
        }
    }

    public async Task ExtractAsync(
        ViewConfig view,
        Bbox bounds,
        string destinationParquet,
        IProgress<SourceExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await defaultClient.QueryToFileAsync(
                CanonicalExtract(view, HilbertOrder(bounds))
                + ResourceSettings(includeSortSpill: true)
                + "\nFORMAT Parquet",
                destinationParquet,
                progress,
                ct);
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw Classify(error, ct, "source.query");
        }
    }

    private string ResourceSettings(bool includeSortSpill = false)
    {
        var settings = new List<string>();
        if (_resources.CpuCores > 0) settings.Add($"max_threads = {_resources.CpuCores}");
        if (includeSortSpill)
            settings.Add($"max_bytes_before_external_sort = {_resources.SourceSpillThresholdBytes}");
        return settings.Count == 0 ? "" : "\nSETTINGS " + string.Join(",\n         ", settings);
    }

    private async Task<T> WithClientAsync<T>(
        SourceConnectionContext connection,
        Func<ClickHouseClient, CancellationToken, Task<T>> operation,
        CancellationToken ct,
        TimeSpan? timeout = null,
        string errorField = "connection")
    {
        using var timeoutSource = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } duration) timeoutSource!.CancelAfter(duration);
        var token = timeoutSource?.Token ?? ct;
        try
        {
            using var lease = OpenClient(connection);
            return await operation(lease.Client, token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            throw new SourceOperationException(
                SourceErrorKind.TransientFailure,
                $"The source operation exceeded its {timeout?.TotalSeconds:0.#}-second duration budget.",
                new Dictionary<string, string[]> { [errorField] = ["Reduce the query scope or increase source selectivity."] },
                transient: true,
                innerException: error);
        }
        catch (Exception error)
        {
            throw Classify(error, ct, errorField);
        }
    }

    private ClientLease OpenClient(SourceConnectionContext connection)
    {
        bool configured = connection.Settings.Count > 0 || connection.Secrets.Count > 0;
        if (!configured) return new ClientLease(defaultClient, dispose: false);

        var defaults = defaultOptions.Value;
        string url = Value(connection.Settings, "url") ?? defaults.BaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new SourceOperationException(
                SourceErrorKind.Configuration,
                "ClickHouse HTTP URL is invalid.",
                new Dictionary<string, string[]> { ["settings.url"] = ["Enter an absolute HTTP or HTTPS URL."] });
        var options = new ClickHouseOptions
        {
            BaseUrl = uri.ToString(),
            User = Value(connection.Settings, "user") ?? defaults.User,
            Password = Value(connection.Secrets, "password") ?? defaults.Password,
        };
        return new ClientLease(new ClickHouseClient(httpClients.CreateClient("Colossus.ClickHouse"), options), dispose: true);
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string PreviewQuery(DatasetSource dataset, SourcePreviewBudget budget)
    {
        string seconds = Math.Max(1, budget.MaxDuration.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        return $"""
            {CanonicalProjection(dataset)}
            LIMIT {budget.MaxRows + 1}
            SETTINGS max_execution_time = {seconds},
                     max_result_bytes = {budget.MaxResultBytes},
                     max_rows_to_read = {budget.MaxScannedRows},
                     read_overflow_mode = 'throw'
            FORMAT JSON
            """;
    }

    private static SourcePreview ParsePreview(
        string response,
        DatasetSource dataset,
        SourcePreviewBudget budget,
        TimeSpan elapsed)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        var data = root.GetProperty("data").EnumerateArray().ToArray();
        if (data.Length == 0)
            throw new SourceOperationException(
                SourceErrorKind.Configuration,
                "The preview query returned no geometry.",
                new Dictionary<string, string[]> { ["source.query"] = ["Return at least one mapped geometry row."] });

        bool truncated = data.Length > budget.MaxRows;
        var sample = data.Take(budget.MaxRows).ToArray();
        var rows = sample.Select(ReadRow).ToArray();
        var coordinates = sample.Select(ReadCoordinate).ToArray();
        ValidateCoordinates(coordinates, dataset.SourceGeometry());

        var bounds = new Bbox(
            coordinates.Min(point => point.X),
            coordinates.Min(point => point.Y),
            coordinates.Max(point => point.X),
            coordinates.Max(point => point.Y));
        var columns = root.GetProperty("meta").EnumerateArray().Select(column =>
        {
            string name = column.GetProperty("name").GetString() ?? "unknown";
            string type = column.GetProperty("type").GetString() ?? "Unknown";
            string role = name is TileSchema.X or TileSchema.Y or TileSchema.Geometry or TileSchema.PartOffsets
                ? "geometry"
                : dataset.Channels.FirstOrDefault(channel => channel.Name == name)?.Role.ToString().ToLowerInvariant()
                    ?? "value";
            return new SourcePreviewColumn(name, type, type.StartsWith("Nullable(", StringComparison.Ordinal), role);
        }).ToArray();
        long bytes = Encoding.UTF8.GetByteCount(response);
        if (bytes > budget.MaxResultBytes + 64 * 1024)
            throw new SourceOperationException(SourceErrorKind.TransientFailure, "The preview exceeded its byte budget.");

        bool geographic = dataset.Geometry.Kind != GeometryKind.Xy || dataset.Geometry.Geographic;
        return new SourcePreview(
            columns,
            rows,
            new SourceGeometryPreview(
                dataset.Geometry.Kind,
                geographic,
                bounds,
                rows.Length,
                coordinates.Distinct().Count()),
            truncated,
            bytes,
            elapsed);
    }

    private static IReadOnlyDictionary<string, string?> ReadRow(JsonElement row) => row.EnumerateObject()
        .ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            },
            StringComparer.Ordinal);

    private static (double X, double Y) ReadCoordinate(JsonElement row) =>
        (ReadNumber(row.GetProperty(TileSchema.X)), ReadNumber(row.GetProperty(TileSchema.Y)));

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return double.NaN;
    }

    private static void ValidateCoordinates(
        IReadOnlyList<(double X, double Y)> points,
        (GeometryKind Kind, bool Geographic) geometry)
    {
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw GeometryError("Mapped geometry contains null, empty, or non-finite coordinates.");
        if (geometry.Geographic && points.Any(point => point.X is < -180 or > 180 || point.Y is < -90 or > 90))
            throw GeometryError("Mapped geographic coordinates fall outside longitude/latitude bounds; verify that the columns are not swapped.");
    }

    private static SourceOperationException GeometryError(string message) => new(
        SourceErrorKind.Type,
        message,
        new Dictionary<string, string[]> { ["source.geometry"] = [message] });

    private static string Bounds(ViewConfig view)
    {
        var geometry = GeometrySqlFactory.Build(view.Source.Geometry);
        return $"""
            SELECT {geometry.X} AS {TileSchema.X}, {geometry.Y} AS {TileSchema.Y}
            FROM (
            {Decoded(view.Source.Query, geometry)}
            ) AS src{Where(view.BakeFilters)}
            """;
    }

    private static string CanonicalExtract(ViewConfig view, string order) =>
        CanonicalProjection(DatasetSource.FromSnapshot(view.Source), view.BakeFilters) + $"\nORDER BY {order}";

    private static string CanonicalProjection(DatasetSource source, IReadOnlyList<string>? filters = null)
    {
        var geometry = GeometrySqlFactory.Build(source.Geometry);
        string extras = string.Concat(geometry.Extras.Select(extra => $",\n       {extra.Expr} AS {extra.Alias}"));
        string channels = string.Concat(source.Channels.Select(channel => $",\n       {Cast(channel)} AS `{channel.Name}`"));
        return $"""
            SELECT {geometry.X} AS {TileSchema.X}, {geometry.Y} AS {TileSchema.Y}{extras}{channels}
            FROM (
            {Decoded(source.Query, geometry)}
            ) AS src{Where(filters)}
            """;
    }

    private static string Where(IReadOnlyList<string>? filters) => filters is { Count: > 0 }
        ? "\nWHERE " + string.Join(" AND ", filters.Select(predicate => $"({predicate})"))
        : "";

    private static string Decoded(string query, GeometrySql geometry)
    {
        if (geometry.Prelude.Count == 0) return query;
        string columns = string.Join(",\n       ", geometry.Prelude.Select(column => $"{column.Expr} AS {column.Alias}"));
        return $"""
            SELECT *,
                   {columns}
            FROM (
            {query}
            ) AS src0
            """;
    }

    private static string Cast(ChannelSpec channel) => channel.Type switch
    {
        ChannelType.F32 => $"toFloat32({channel.Column})",
        ChannelType.F64 => $"toFloat64({channel.Column})",
        ChannelType.U8 => $"toUInt8({channel.Column})",
        ChannelType.U16 => $"toUInt16({channel.Column})",
        ChannelType.I32 => $"toInt32({channel.Column})",
        ChannelType.I64 => $"toInt64({channel.Column})",
        ChannelType.Date => $"toDate({channel.Column})",
        ChannelType.Dict => $"COALESCE(toString({channel.Column}), 'null')",
        _ => channel.Column,
    };

    private static string HilbertOrder(Bbox bounds)
    {
        if (bounds.SpanX <= 0 || bounds.SpanY <= 0) return "rand()";
        return $"hilbertEncode({Grid(TileSchema.X, bounds.MinX, bounds.SpanX)}, {Grid(TileSchema.Y, bounds.MinY, bounds.SpanY)})";

        static string Grid(string column, double minimum, double span) =>
            $"toUInt32(least(65535, greatest(0, ({column} - {Sql.Lit(minimum)}) / {Sql.Lit(span)} * 65535)))";
    }

    private static SourceOperationException Classify(Exception error, CancellationToken ct, string field)
    {
        if (error is SourceOperationException sourceError) return sourceError;
        if (error is OperationCanceledException && ct.IsCancellationRequested)
            return new SourceOperationException(SourceErrorKind.Cancellation, "The source operation was cancelled.", innerException: error);
        if (error is HttpRequestException)
            return new SourceOperationException(
                SourceErrorKind.Connectivity,
                "The ClickHouse endpoint could not be reached.",
                new Dictionary<string, string[]> { [field] = [error.Message] },
                transient: true,
                innerException: error);
        if (error is ClickHouseResponseTooLargeException)
            return new SourceOperationException(
                SourceErrorKind.TransientFailure,
                error.Message,
                new Dictionary<string, string[]> { ["preview"] = ["Reduce the preview row or column footprint."] },
                transient: true,
                innerException: error);
        if (error is ClickHouseRequestException clickHouse)
        {
            bool typeFailure = ContainsAny(clickHouse.ResponseBody,
                "TYPE_MISMATCH", "ILLEGAL_TYPE", "CANNOT_CONVERT", "CANNOT_PARSE", "BAD_ARGUMENTS");
            bool transient = clickHouse.StatusCode == HttpStatusCode.TooManyRequests || (int)clickHouse.StatusCode >= 500;
            var kind = transient ? SourceErrorKind.TransientFailure : typeFailure ? SourceErrorKind.Type :
                clickHouse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? SourceErrorKind.Configuration
                    : SourceErrorKind.Query;
            string target = typeFailure ? "source.geometry" : field;
            return new SourceOperationException(
                kind,
                clickHouse.Message,
                new Dictionary<string, string[]> { [target] = [clickHouse.ResponseBody.Trim()] },
                transient,
                clickHouse);
        }
        return new SourceOperationException(
            SourceErrorKind.Query,
            error.Message,
            new Dictionary<string, string[]> { [field] = [error.Message] },
            innerException: error);
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed class ClientLease(ClickHouseClient client, bool dispose) : IDisposable
    {
        public ClickHouseClient Client { get; } = client;
        public void Dispose()
        {
            if (dispose) Client.Dispose();
        }
    }
}

internal static class DatasetSourceGeometryExtensions
{
    public static (GeometryKind Kind, bool Geographic) SourceGeometry(this DatasetSource source) =>
        (source.Geometry.Kind, source.Geometry.Kind != GeometryKind.Xy || source.Geometry.Geographic);
}
