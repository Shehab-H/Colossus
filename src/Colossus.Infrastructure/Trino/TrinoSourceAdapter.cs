using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Sources;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.Trino;

/// <summary>Federated Trino source adapter. Trino pages are normalized one row at a time into an
/// on-disk DuckDB staging table so probe and extraction never retain the complete source result.</summary>
public sealed class TrinoSourceAdapter : ISourceAdapter
{
    private readonly TrinoOptions _defaults;
    private readonly TrinoClient _client;

    public TrinoSourceAdapter(IOptions<TrinoOptions> defaults, IHttpClientFactory clients)
    {
        _defaults = defaults.Value;
        _client = new TrinoClient(clients.CreateClient("Colossus.Trino"));
        var fields = new List<SourceConfigurationField>
        {
            new("url", "Coordinator URL", Required: true, Placeholder: _defaults.BaseUrl),
            new("catalog", "Catalog", Required: true, Placeholder: _defaults.Catalog),
            new("schema", "Schema", Required: true, Placeholder: _defaults.Schema),
            new("user", "User", Required: true, Placeholder: _defaults.User),
            new("sessionProperties", "Session properties (JSON)", Required: false, Placeholder: "{\"query_max_execution_time\":\"10m\"}"),
            new("maxPageBytes", "Page byte limit", Required: false, Placeholder: _defaults.MaxPageBytes.ToString(CultureInfo.InvariantCulture)),
            new("maxRetries", "Transient retries", Required: false, Placeholder: _defaults.MaxRetries.ToString(CultureInfo.InvariantCulture)),
        };
        string[] profiles = _defaults.HeaderProfiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (profiles.Length > 0)
            fields.Add(new SourceConfigurationField("headerProfile", "Header profile", Required: false, AllowedValues: profiles));

        Descriptor = new SourceAdapterDescriptor(
            Id: "trino",
            Name: "Trino",
            Capabilities: new SourceAdapterCapabilities(true, true, true, true, true),
            ConfigurationFields: fields,
            GeometryKinds: [GeometryKind.Xy, GeometryKind.LonLat, GeometryKind.Quadkey, GeometryKind.Wkt],
            DatasetInputLabel: "Trino SQL",
            DatasetInputDescription: "Enter one read-only SELECT, WITH, TABLE, or VALUES statement; Colossus safely projects and bounds it.",
            DatasetInputPlaceholder: "SELECT lon, lat, value FROM lakehouse.analytics.coverage");
    }

    public SourceAdapterDescriptor Descriptor { get; }

    public async Task<SourceConnectionValidation> ValidateConnectionAsync(
        SourceConnectionContext connection,
        CancellationToken ct = default)
    {
        try
        {
            var configured = Configure(connection.Settings);
            var result = await ReadRawAsync(configured, "SELECT version(), current_catalog, current_schema", ct);
            var row = result.Rows.SingleOrDefault()
                ?? throw new TrinoProtocolException("Trino validation returned no result row.");
            var details = new Dictionary<string, string>
            {
                ["serverVersion"] = Text(row[0]),
                ["catalog"] = Text(row[1]),
                ["schema"] = Text(row[2]),
            };
            if (result.Warnings.Count > 0) details["warnings"] = string.Join(" | ", result.Warnings);
            return new SourceConnectionValidation("Connection succeeded.", details);
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Trino validation was cancelled.", error, ct);
            throw Classify(error, "connection");
        }
    }

    public async Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
        SourceConnectionContext connection,
        CancellationToken ct = default)
    {
        try
        {
            var result = await ReadRawAsync(Configure(connection.Settings), """
                SELECT table_schema, table_name, table_type
                FROM information_schema.tables
                WHERE table_schema <> 'information_schema'
                ORDER BY table_schema, table_name
                LIMIT 200
                """, ct);
            return result.Rows.Select(row => new SourceLocation(Text(row[0]), Text(row[1]), Text(row[2]))).ToArray();
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Trino discovery was cancelled.", error, ct);
            throw Classify(error, "connection");
        }
    }

    public async Task<SourceSchema> InspectSchemaAsync(
        SourceSchemaRequest request,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SourcePreviewBudget.Default.MaxDuration);
        try
        {
            string query = NormalizeSql(request.Query);
            var read = await ReadRawAsync(
                Configure(request.Connection.Settings),
                $"SELECT * FROM (\n{query}\n) AS colossus_source LIMIT 0",
                timeout.Token);
            return new SourceSchema(
                read.Columns.Select(column => new SourceSchemaColumn(
                    column.Name,
                    column.Type,
                    Nullable: true)).ToArray(),
                read.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            throw new SourceOperationException(
                SourceErrorKind.TransientFailure,
                "Trino schema inspection exceeded its 10-second duration budget.",
                new Dictionary<string, string[]> { ["query"] = ["Simplify the query used for schema inspection."] },
                transient: true,
                innerException: error);
        }
        catch (Exception error)
        {
            throw Classify(error, "query");
        }
    }

    public async Task<SourcePreview> PreviewAsync(SourcePreviewRequest request, CancellationToken ct = default)
    {
        var budget = request.Budget.ConstrainTo(SourcePreviewBudget.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget.MaxDuration);
        try
        {
            var timer = Stopwatch.StartNew();
            var projection = Projection(request.Dataset);
            string query = $"{projection.SelectFrom(NormalizeSql(request.Dataset.Query))}\nLIMIT {budget.MaxRows + 1}";
            var read = await ReadCanonicalAsync(
                Configure(request.Connection.Settings),
                query,
                request.Dataset,
                projection,
                maxScannedRows: budget.MaxScannedRows,
                maxResultBytes: budget.MaxResultBytes,
                onRow: null,
                timeout.Token);
            if (read.Rows.Count == 0)
                throw new SourceOperationException(
                    SourceErrorKind.Configuration,
                    "The Trino preview returned no geometry.",
                    new Dictionary<string, string[]> { ["source.query"] = ["Choose a query containing at least one mapped geometry row."] });

            bool truncated = read.Rows.Count > budget.MaxRows;
            CanonicalRow[] rows = read.Rows.Take(budget.MaxRows).ToArray();
            var bounds = Bounds(rows.Select(row => row.Geometry));
            return new SourcePreview(
                PreviewColumns(request.Dataset, projection, read.SourceTypes),
                rows.Select(row => PreviewRow(row, request.Dataset)).ToArray(),
                new SourceGeometryPreview(
                    request.Dataset.Geometry.Kind,
                    Geographic(request.Dataset.Geometry),
                    bounds,
                    rows.Length,
                    rows.Select(row => (row.Geometry.X, row.Geometry.Y)).Distinct().Count()),
                truncated,
                read.PayloadBytes,
                timer.Elapsed,
                read.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            throw new SourceOperationException(
                SourceErrorKind.TransientFailure,
                $"The Trino preview exceeded its {budget.MaxDuration.TotalSeconds:0.#}-second duration budget.",
                new Dictionary<string, string[]> { ["preview"] = ["Reduce the query scope or improve source selectivity."] },
                transient: true,
                innerException: error);
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Trino preview was cancelled.", error, ct);
            throw Classify(error, "source.query");
        }
    }

    public async Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default)
    {
        try
        {
            return await WithSessionAsync(async (db, token) =>
            {
                await MaterializeAsync(db, view, progress: null, token);
                using var command = db.Connection.CreateCommand();
                command.CommandText = $"SELECT min(x), max(x), min(y), max(y), count(*), count(DISTINCT (x, y)) FROM canonical{Where(view.BakeFilters)}";
                using var reader = await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                long count = reader.GetInt64(4);
                if (count == 0) return new SourceBounds(default, 0, 0);
                double Number(int index) => Convert.ToDouble(reader.GetValue(index), CultureInfo.InvariantCulture);
                return new SourceBounds(new Bbox(Number(0), Number(2), Number(1), Number(3)), count, reader.GetInt64(5));
            }, ct);
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Trino probe was cancelled.", error, ct);
            throw Classify(error, "source.query");
        }
    }

    public async Task ExtractAsync(
        ViewConfig view,
        Bbox bounds,
        string destinationParquet,
        IProgress<SourceExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        string destination = Path.GetFullPath(destinationParquet);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("A destination directory is required.", nameof(destinationParquet));
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        Materialization? materialization = null;
        var dataset = DatasetSource.FromSnapshot(view.Source);
        try
        {
            await WithSessionAsync(async (db, token) =>
            {
                materialization = await MaterializeAsync(db, view, progress, token);
                using var command = db.Connection.CreateCommand();
                command.CommandText = $"COPY (SELECT * FROM canonical{Where(view.BakeFilters)} ORDER BY {CanonicalOrder(bounds, dataset)}) TO {Literal(partial)} (FORMAT PARQUET)";
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, ct);
            File.Move(partial, destination, overwrite: true);
            progress?.Report(new SourceExtractProgress(
                Math.Max(materialization?.PayloadBytes ?? 0, new FileInfo(destination).Length),
                Completed: true,
                RowsRead: materialization?.Rows));
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException("Trino extraction was cancelled.", error, ct);
            throw Classify(error, "source.query");
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private async Task<Materialization> MaterializeAsync(
        DuckDbSession db,
        ViewConfig view,
        IProgress<SourceExtractProgress>? progress,
        CancellationToken ct)
    {
        var dataset = DatasetSource.FromSnapshot(view.Source);
        var projection = Projection(dataset);
        db.Exec($"CREATE TABLE canonical ({CanonicalSchema(dataset)})");
        using var appender = db.Connection.CreateAppender("canonical");
        long count = 0;
        long payloadBytes = 0;
        await ReadCanonicalAsync(
            Configure(view.Source.Settings),
            projection.SelectFrom(NormalizeSql(dataset.Query)),
            dataset,
            projection,
            maxScannedRows: null,
            maxResultBytes: null,
            row =>
            {
                var target = appender.CreateRow();
                target.AppendValue(row.Geometry.X).AppendValue(row.Geometry.Y);
                if (row.Geometry.Coordinates is not null)
                    target.AppendValue(row.Geometry.Coordinates).AppendValue(row.Geometry.PartOffsets!);
                foreach (object? value in row.Channels) Append(target, value);
                target.EndRow();
                count++;
            },
            ct,
            (bytes, rows) =>
            {
                payloadBytes = bytes;
                progress?.Report(new SourceExtractProgress(bytes, RowsRead: rows));
            });
        appender.Close();
        return new Materialization(count, payloadBytes);
    }

    private async Task<CanonicalRead> ReadCanonicalAsync(
        TrinoConnection connection,
        string sql,
        DatasetSource dataset,
        TrinoProjection projection,
        long? maxScannedRows,
        long? maxResultBytes,
        Action<CanonicalRow>? onRow,
        CancellationToken ct,
        Action<long, long>? onProgress = null)
    {
        var rows = new List<CanonicalRow>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        string[] sourceTypes = new string[projection.Columns.Count];
        long payloadBytes = 0;
        long rowCount = 0;
        await foreach (var page in _client.QueryAsync(connection, sql, ct))
        {
            payloadBytes = checked(payloadBytes + page.PayloadBytes);
            if (maxResultBytes is { } resultLimit && payloadBytes > resultLimit)
                throw new SourceOperationException(
                    SourceErrorKind.TransientFailure,
                    "The Trino preview exceeded its result-byte ceiling.",
                    new Dictionary<string, string[]> { ["preview"] = ["Reduce the projected columns or row footprint."] },
                    transient: true);
            if (maxScannedRows is { } scanLimit && page.Stats.ProcessedRows > scanLimit)
                throw new SourceOperationException(
                    SourceErrorKind.TransientFailure,
                    $"The Trino query scanned more than the {scanLimit:N0}-row preview ceiling.",
                    new Dictionary<string, string[]> { ["preview"] = ["Add a selective predicate or query a smaller partition."] },
                    transient: true);
            foreach (string warning in page.Warnings) warnings.Add(warning);
            if (page.Columns.Count > 0)
            {
                if (page.Columns.Count != projection.Columns.Count)
                    throw new SourceOperationException(SourceErrorKind.Type, "Trino returned an unexpected projected column count.");
                for (int index = 0; index < page.Columns.Count; index++) sourceTypes[index] = page.Columns[index].Type;
            }
            foreach (JsonElement[] values in page.Rows)
            {
                if (values.Length != projection.Columns.Count)
                    throw new SourceOperationException(SourceErrorKind.Type, "Trino returned a row with an unexpected field count.");
                var canonical = Canonicalize(values, projection, dataset, sourceTypes);
                if (onRow is null) rows.Add(canonical); else onRow(canonical);
                rowCount++;
            }
            onProgress?.Invoke(payloadBytes, rowCount);
        }
        return new CanonicalRead(rows, sourceTypes, [.. warnings], payloadBytes);
    }

    private async Task<RawRead> ReadRawAsync(TrinoConnection connection, string sql, CancellationToken ct)
    {
        var rows = new List<JsonElement[]>();
        var columns = new List<TrinoColumn>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var page in _client.QueryAsync(connection, sql, ct))
        {
            if (page.Columns.Count > 0 && columns.Count == 0) columns.AddRange(page.Columns);
            rows.AddRange(page.Rows);
            foreach (string warning in page.Warnings) warnings.Add(warning);
        }
        return new RawRead(columns, rows, [.. warnings]);
    }

    private TrinoConnection Configure(IReadOnlyDictionary<string, string> settings)
    {
        string rawUrl = Value(settings, "url") ?? _defaults.BaseUrl;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var coordinator) || coordinator.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(coordinator.UserInfo))
            throw Configuration("settings.url", "Enter an absolute HTTP or HTTPS coordinator URL without embedded credentials.");
        string catalog = Name(Value(settings, "catalog") ?? _defaults.Catalog, "settings.catalog");
        string schema = Name(Value(settings, "schema") ?? _defaults.Schema, "settings.schema");
        string user = Header(Value(settings, "user") ?? _defaults.User, "settings.user");
        var sessions = ParseSessionProperties(Value(settings, "sessionProperties"));
        int retries = BoundedInt(settings, "maxRetries", _defaults.MaxRetries, 0, _defaults.MaxRetries);
        long pageBytes = BoundedLong(settings, "maxPageBytes", _defaults.MaxPageBytes, 64 * 1024, _defaults.MaxPageBytes);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Value(settings, "headerProfile") is { } profile)
        {
            if (!_defaults.HeaderProfiles.TryGetValue(profile, out var configured))
                throw Configuration("settings.headerProfile", $"Unknown Trino header profile '{profile}'.");
            foreach (var pair in configured)
            {
                if (pair.Key.StartsWith("X-Trino-", StringComparison.OrdinalIgnoreCase))
                    throw Configuration("settings.headerProfile", "Header profiles cannot override protocol-owned X-Trino headers.");
                headers[pair.Key] = Header(pair.Value, "settings.headerProfile");
            }
        }
        return new TrinoConnection(coordinator, user, catalog, schema, sessions, headers, retries, pageBytes);
    }

    private static IReadOnlyDictionary<string, string> ParseSessionProperties(string? json)
    {
        if (json is null) return new Dictionary<string, string>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Expected an object.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                string key = Name(property.Name, "settings.sessionProperties");
                string value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
                result[key] = Header(value, "settings.sessionProperties");
            }
            return result;
        }
        catch (SourceOperationException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new SourceOperationException(
                SourceErrorKind.Configuration,
                "Trino session properties must be a JSON object of name/value pairs.",
                new Dictionary<string, string[]> { ["settings.sessionProperties"] = [error.Message] },
                innerException: error);
        }
    }

    private static string NormalizeSql(string sql)
    {
        string normalized = sql.Trim();
        if (normalized.EndsWith(';')) normalized = normalized[..^1].TrimEnd();
        if (normalized.Contains(';'))
            throw Configuration("source.query", "Trino datasets accept exactly one statement.");
        string first = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant() ?? "";
        if (first is not ("SELECT" or "WITH" or "TABLE" or "VALUES"))
            throw Configuration("source.query", "Trino dataset SQL must be read-only SELECT, WITH, TABLE, or VALUES.");
        return normalized;
    }

    private async Task<T> WithSessionAsync<T>(Func<DuckDbSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        string root = Path.GetFullPath(_defaults.WorkDirectory);
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            using var db = DuckDbSession.OnDisk(work);
            return await operation(db, ct);
        }
        finally
        {
            string resolved = Path.GetFullPath(work);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static CanonicalRow Canonicalize(
        JsonElement[] values,
        TrinoProjection projection,
        DatasetSource dataset,
        IReadOnlyList<string> sourceTypes)
    {
        object? Value(string column) => JsonValue(values[projection.Indexes[column]]);
        var geometry = CanonicalGeometryNormalizer.Normalize(dataset.Geometry, Value);
        object?[] channels = dataset.Channels.Select(channel =>
        {
            int index = projection.Indexes[channel.Column];
            return ConvertChannel(JsonValue(values[index]), channel, sourceTypes.ElementAtOrDefault(index) ?? "unknown");
        }).ToArray();
        return new CanonicalRow(geometry, channels);
    }

    private static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out double number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => value.GetRawText(),
    };

    private static object? ConvertChannel(object? value, ChannelSpec channel, string sourceType)
    {
        if (value is null or DBNull) return channel.Type == ChannelType.Dict ? "null" : null;
        try
        {
            return channel.Type switch
            {
                ChannelType.F32 => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                ChannelType.F64 => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                ChannelType.U8 => Convert.ToByte(value, CultureInfo.InvariantCulture),
                ChannelType.U16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                ChannelType.I32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                ChannelType.I64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                ChannelType.Dict => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
                ChannelType.Date => value switch
                {
                    DateOnly date => date,
                    DateTime dateTime => DateOnly.FromDateTime(dateTime),
                    _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                },
                _ => throw new NotSupportedException($"Unsupported channel type '{channel.Type}'."),
            };
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException or NotSupportedException)
        {
            string message = $"Channel '{channel.Name}' from Trino type '{sourceType}' cannot be converted to {channel.Type.ToString().ToLowerInvariant()}.";
            throw new SourceOperationException(
                SourceErrorKind.Type,
                message,
                new Dictionary<string, string[]> { [$"source.channels.{channel.Name}"] = [message] },
                innerException: error);
        }
    }

    private static TrinoProjection Projection(DatasetSource dataset)
    {
        var columns = new List<string>();
        void Add(string? column)
        {
            if (!string.IsNullOrWhiteSpace(column) && !columns.Contains(column, StringComparer.Ordinal)) columns.Add(column);
        }
        Add(dataset.Geometry.Kind switch { GeometryKind.Xy => dataset.Geometry.X, GeometryKind.LonLat => dataset.Geometry.Lon, _ => dataset.Geometry.Column });
        if (dataset.Geometry.Kind == GeometryKind.Xy) Add(dataset.Geometry.Y);
        if (dataset.Geometry.Kind == GeometryKind.LonLat) Add(dataset.Geometry.Lat);
        foreach (var channel in dataset.Channels) Add(channel.Column);
        var indexes = columns.Select((column, index) => (column, index)).ToDictionary(pair => pair.column, pair => pair.index, StringComparer.Ordinal);
        return new TrinoProjection(columns, indexes);
    }

    private static IReadOnlyList<SourcePreviewColumn> PreviewColumns(
        DatasetSource dataset,
        TrinoProjection projection,
        IReadOnlyList<string> sourceTypes)
    {
        var columns = new List<SourcePreviewColumn>
        {
            new(TileSchema.X, "real", false, "geometry"),
            new(TileSchema.Y, "real", false, "geometry"),
        };
        if (dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt)
        {
            columns.Add(new SourcePreviewColumn(TileSchema.Geometry, "array(real)", false, "geometry"));
            columns.Add(new SourcePreviewColumn(TileSchema.PartOffsets, "array(integer)", false, "geometry"));
        }
        columns.AddRange(dataset.Channels.Select(channel => new SourcePreviewColumn(
            channel.Name,
            sourceTypes.ElementAtOrDefault(projection.Indexes[channel.Column]) ?? "unknown",
            channel.Type != ChannelType.Dict,
            channel.Role.ToString().ToLowerInvariant())));
        return columns;
    }

    private static IReadOnlyDictionary<string, string?> PreviewRow(CanonicalRow row, DatasetSource dataset)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TileSchema.X] = row.Geometry.X.ToString("R", CultureInfo.InvariantCulture),
            [TileSchema.Y] = row.Geometry.Y.ToString("R", CultureInfo.InvariantCulture),
        };
        if (row.Geometry.Coordinates is not null)
        {
            values[TileSchema.Geometry] = JsonSerializer.Serialize(row.Geometry.Coordinates);
            values[TileSchema.PartOffsets] = JsonSerializer.Serialize(row.Geometry.PartOffsets);
        }
        for (int index = 0; index < dataset.Channels.Count; index++) values[dataset.Channels[index].Name] = Format(row.Channels[index]);
        return values;
    }

    private static void Append(IDuckDBAppenderRow row, object? value)
    {
        switch (value)
        {
            case null: row.AppendNullValue(); break;
            case float typed: row.AppendValue(typed); break;
            case double typed: row.AppendValue(typed); break;
            case byte typed: row.AppendValue(typed); break;
            case ushort typed: row.AppendValue(typed); break;
            case int typed: row.AppendValue(typed); break;
            case long typed: row.AppendValue(typed); break;
            case string typed: row.AppendValue(typed); break;
            case DateOnly typed: row.AppendValue(typed); break;
            default: throw new NotSupportedException($"Unsupported canonical value '{value.GetType().Name}'.");
        }
    }

    private static string CanonicalSchema(DatasetSource dataset)
    {
        var columns = new List<string> { "x FLOAT NOT NULL", "y FLOAT NOT NULL" };
        if (dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt)
        {
            columns.Add("geometry FLOAT[] NOT NULL");
            columns.Add("part_offsets INTEGER[] NOT NULL");
        }
        columns.AddRange(dataset.Channels.Select(channel => $"{Identifier(channel.Name)} {DuckType(channel.Type)}"));
        return string.Join(", ", columns);
    }

    private static string DuckType(ChannelType type) => type switch
    {
        ChannelType.F32 => "FLOAT",
        ChannelType.F64 => "DOUBLE",
        ChannelType.U8 => "UTINYINT",
        ChannelType.U16 => "USMALLINT",
        ChannelType.I32 => "INTEGER",
        ChannelType.I64 => "BIGINT",
        ChannelType.Dict => "VARCHAR",
        ChannelType.Date => "DATE",
        _ => throw new NotSupportedException($"Unsupported channel type '{type}'."),
    };

    private static string Where(IReadOnlyList<string>? filters) =>
        filters is { Count: > 0 } ? " WHERE " + string.Join(" AND ", filters.Select(filter => $"({filter})")) : "";

    private static Bbox Bounds(IEnumerable<CanonicalGeometry> values)
    {
        CanonicalGeometry[] geometries = values.ToArray();
        return new Bbox(geometries.Min(value => value.X), geometries.Min(value => value.Y), geometries.Max(value => value.X), geometries.Max(value => value.Y));
    }

    private static bool Geographic(GeometrySpec geometry) => geometry.Kind != GeometryKind.Xy || geometry.Geographic;
    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? "" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string Name(string value, string field)
    {
        string trimmed = value.Trim();
        if (trimmed.Length is 0 or > 256 || trimmed.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || character is ',' or '='))
            throw Configuration(field, "Use a non-empty Trino identifier without whitespace, commas, equals signs, or control characters.");
        return trimmed;
    }

    private static string Header(string value, string field)
    {
        if (value.Length is 0 or > 8192 || value.Any(character => character is '\r' or '\n' or '\0'))
            throw Configuration(field, "The value contains invalid HTTP header characters or exceeds 8 KiB.");
        return value;
    }

    private static int BoundedInt(IReadOnlyDictionary<string, string> settings, string key, int fallback, int minimum, int maximum)
    {
        if (Value(settings, key) is not { } raw) return fallback;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            throw Configuration($"settings.{key}", $"Enter a value from {minimum:N0} to {maximum:N0}.");
        return value;
    }

    private static long BoundedLong(IReadOnlyDictionary<string, string> settings, string key, long fallback, long minimum, long maximum)
    {
        if (Value(settings, key) is not { } raw) return fallback;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value < minimum || value > maximum)
            throw Configuration($"settings.{key}", $"Enter a value from {minimum:N0} to {maximum:N0}.");
        return value;
    }

    private static SourceOperationException Configuration(string field, string message) => new(
        SourceErrorKind.Configuration,
        message,
        new Dictionary<string, string[]> { [field] = [message] });

    private static SourceOperationException Classify(Exception error, string field)
    {
        if (error is SourceOperationException source) return source;
        if (error is TrinoResponseTooLargeException)
            return new SourceOperationException(SourceErrorKind.TransientFailure, error.Message, new Dictionary<string, string[]> { [field] = [error.Message] }, transient: true, innerException: error);
        if (error is TrinoProtocolException trino)
        {
            var details = new List<string> { trino.Message };
            if (trino.QueryId is not null) details.Add($"Query ID: {trino.QueryId}");
            if (trino.ErrorName is not null) details.Add($"Error: {trino.ErrorName}");
            if (trino.ErrorType is not null) details.Add($"Type: {trino.ErrorType}");
            var kind = trino.ErrorType == "USER_ERROR" ? SourceErrorKind.Query : trino.Transient ? SourceErrorKind.TransientFailure : SourceErrorKind.Connectivity;
            return new SourceOperationException(kind, trino.Message, new Dictionary<string, string[]> { [field] = [.. details] }, trino.Transient, trino);
        }
        if (error is HttpRequestException)
            return new SourceOperationException(SourceErrorKind.Connectivity, "The Trino coordinator could not be reached.", new Dictionary<string, string[]> { [field] = [error.Message] }, transient: true, innerException: error);
        return new SourceOperationException(SourceErrorKind.Query, error.Message, new Dictionary<string, string[]> { [field] = [error.Message] }, innerException: error);
    }

    private static string Identifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string Literal(string value) => $"'{value.Replace("'", "''")}'";

    private static string MortonOrder(Bbox bounds)
    {
        if (bounds.SpanX <= 0 || bounds.SpanY <= 0) return "x, y";
        string Grid(string column, double minimum, double span) =>
            $"CAST(least(65535, greatest(0, ({column} - {Sql.Lit(minimum)}) / {Sql.Lit(span)} * 65535)) AS UBIGINT)";
        string Spread(string value)
        {
            string a = $"(({value} | ({value} << 8)) & 16711935)";
            string b = $"(({a} | ({a} << 4)) & 252645135)";
            string c = $"(({b} | ({b} << 2)) & 858993459)";
            return $"(({c} | ({c} << 1)) & 1431655765)";
        }
        return $"({Spread(Grid("x", bounds.MinX, bounds.SpanX))} | ({Spread(Grid("y", bounds.MinY, bounds.SpanY))} << 1))";
    }

    private static string CanonicalOrder(Bbox bounds, DatasetSource dataset)
    {
        var tieBreakers = new List<string> { "x", "y" };
        if (dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt)
        {
            tieBreakers.Add("geometry");
            tieBreakers.Add("part_offsets");
        }
        tieBreakers.AddRange(dataset.Channels.Select(channel => Identifier(channel.Name)));
        return $"{MortonOrder(bounds)}, {string.Join(", ", tieBreakers)}";
    }

    private sealed record TrinoProjection(IReadOnlyList<string> Columns, IReadOnlyDictionary<string, int> Indexes)
    {
        public string SelectFrom(string query) =>
            $"SELECT {string.Join(", ", Columns.Select(Identifier))} FROM (\n{query}\n) AS colossus_source";
    }

    private sealed record CanonicalRow(CanonicalGeometry Geometry, object?[] Channels);
    private sealed record CanonicalRead(IReadOnlyList<CanonicalRow> Rows, IReadOnlyList<string> SourceTypes, IReadOnlyList<string> Warnings, long PayloadBytes);
    private sealed record Materialization(long Rows, long PayloadBytes);
    private sealed record RawRead(IReadOnlyList<TrinoColumn> Columns, IReadOnlyList<JsonElement[]> Rows, IReadOnlyList<string> Warnings);
}
