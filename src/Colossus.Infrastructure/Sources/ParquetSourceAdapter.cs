using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.DuckDb;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.Sources;

public sealed class ParquetSourceAdapter : ISourceAdapter
{
    private readonly ParquetSourceOptions _options;
    private readonly ParquetSourceLocationResolver _locations;
    private readonly BakeResourceOptions _resources;

    public ParquetSourceAdapter(
        IOptions<ParquetSourceOptions> options,
        IHttpClientFactory httpClients,
        BakeResourceOptions? resources = null)
    {
        _options = options.Value;
        _locations = new ParquetSourceLocationResolver(_options, httpClients);
        _resources = resources ?? new BakeResourceOptions();
        Descriptor = new SourceAdapterDescriptor(
            Id: "parquet",
            Name: "Raw Parquet",
            Capabilities: new SourceAdapterCapabilities(true, true, true, true, true),
            ConfigurationFields:
            [
                new SourceConfigurationField(
                    "provider",
                    "Provider",
                    Required: true,
                    Placeholder: "local",
                    AllowedValues: _locations.ProviderAliases),
            ],
            GeometryKinds: [GeometryKind.Xy, GeometryKind.LonLat, GeometryKind.Quadkey, GeometryKind.Wkt],
            DatasetInputLabel: "Parquet location",
            DatasetInputDescription: "Use a provider-relative file, safe glob, or partitioned dataset root.",
            DatasetInputPlaceholder: "coverage/year=*/part-*.parquet");
    }

    public SourceAdapterDescriptor Descriptor { get; }

    public Task<SourceConnectionValidation> ValidateConnectionAsync(
        SourceConnectionContext connection,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var provider = _locations.ResolveProvider(connection.Settings, out string alias);
            if (provider.Kind.Equals("local", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(provider.BaseLocation))
                throw new SourceOperationException(
                    SourceErrorKind.Configuration,
                    $"Configured Parquet root '{alias}' is unavailable.",
                    new Dictionary<string, string[]> { ["settings.provider"] = ["Ask an administrator to create or mount the configured root."] });
            return Task.FromResult(new SourceConnectionValidation(
                "Parquet provider is ready.",
                new Dictionary<string, string>
                {
                    ["provider"] = alias,
                    ["kind"] = provider.Kind.ToLowerInvariant(),
                }));
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("The Parquet provider validation was cancelled.", error, ct);
            throw Classify(error, ct, "settings.provider");
        }
    }

    public Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
        SourceConnectionContext connection,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var provider = _locations.ResolveProvider(connection.Settings, out string alias);
            return Task.FromResult(_locations.DiscoverLocal(alias, provider));
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Parquet discovery was cancelled.", error, ct);
            throw Classify(error, ct, "settings.provider");
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
            var resolved = await _locations.ResolveAsync(
                request.Connection.Settings, request.Query, snapshot: null, timeout.Token);
            return await WithSessionAsync(resolved, async (db, token) =>
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {ReadParquet(resolved)} LIMIT 0";
                using var reader = await command.ExecuteReaderAsync(token);
                var columns = reader.GetColumnSchema().Select(column => new SourceSchemaColumn(
                    column.ColumnName ?? "unknown",
                    column.DataTypeName ?? column.DataType?.Name ?? "Unknown",
                    column.AllowDBNull != false)).ToArray();
                return new SourceSchema(columns);
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            throw new SourceOperationException(
                SourceErrorKind.TransientFailure,
                "Parquet schema inspection exceeded its 10-second duration budget.",
                new Dictionary<string, string[]> { ["query"] = ["Choose fewer files or a more selective partition."] },
                transient: true,
                innerException: error);
        }
        catch (Exception error)
        {
            throw Classify(error, ct, "query");
        }
    }

    public async Task<SourcePreview> PreviewAsync(SourcePreviewRequest request, CancellationToken ct = default)
    {
        var budget = request.Budget.ConstrainTo(SourcePreviewBudget.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget.MaxDuration);
        try
        {
            var resolved = await _locations.ResolveAsync(
                request.Connection.Settings, request.Dataset.Query, snapshot: null, timeout.Token);
            return await WithSessionAsync(resolved, async (db, token) =>
            {
                var timer = Stopwatch.StartNew();
                long sourceRows = await MetadataRowsAsync(db, resolved, token);
                if (sourceRows > budget.MaxScannedRows)
                    throw new SourceOperationException(
                        SourceErrorKind.TransientFailure,
                        $"The Parquet dataset has {sourceRows:N0} rows, above the {budget.MaxScannedRows:N0}-row preview scan ceiling.",
                        new Dictionary<string, string[]> { ["preview"] = ["Choose a smaller partition or a more selective provider-relative location."] },
                        transient: true);

                var projection = Projection(request.Dataset);
                using var command = db.Connection.CreateCommand();
                command.CommandText = $"{projection.SelectFrom(ReadParquet(resolved))}\nLIMIT {budget.MaxRows + 1}";
                using var reader = await command.ExecuteReaderAsync(token);
                var nullable = reader.GetColumnSchema().Select(column => column.AllowDBNull != false).ToArray();
                var sample = new List<CanonicalRow>(budget.MaxRows + 1);
                long resultBytes = 0;
                while (await reader.ReadAsync(token))
                {
                    var row = Canonicalize(reader, projection, request.Dataset);
                    resultBytes += EstimateBytes(row, request.Dataset);
                    if (resultBytes > budget.MaxResultBytes)
                        throw new SourceOperationException(
                            SourceErrorKind.TransientFailure,
                            "The Parquet preview exceeded its result-byte ceiling.",
                            new Dictionary<string, string[]> { ["preview"] = ["Reduce the mapped channel or geometry footprint."] },
                            transient: true);
                    sample.Add(row);
                }
                if (sample.Count == 0)
                    throw new SourceOperationException(
                        SourceErrorKind.Configuration,
                        "The Parquet preview returned no geometry.",
                        new Dictionary<string, string[]> { ["source.query"] = ["Choose an input containing at least one mapped geometry row."] });

                bool truncated = sample.Count > budget.MaxRows;
                CanonicalRow[] rows = sample.Take(budget.MaxRows).ToArray();
                var bounds = Bounds(rows.Select(row => row.Geometry));
                return new SourcePreview(
                    PreviewColumns(request.Dataset, projection, nullable),
                    rows.Select(row => PreviewRow(row, request.Dataset)).ToArray(),
                    new SourceGeometryPreview(
                        request.Dataset.Geometry.Kind,
                        Geographic(request.Dataset.Geometry),
                        bounds,
                        rows.Length,
                        rows.Select(row => (row.Geometry.X, row.Geometry.Y)).Distinct().Count()),
                    truncated,
                    resultBytes,
                    timer.Elapsed);
            }, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        {
            throw new SourceOperationException(
                SourceErrorKind.TransientFailure,
                $"The Parquet preview exceeded its {budget.MaxDuration.TotalSeconds:0.#}-second duration budget.",
                new Dictionary<string, string[]> { ["preview"] = ["Choose fewer files or a more selective partition."] },
                transient: true,
                innerException: error);
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("The Parquet preview was cancelled.", error, ct);
            throw Classify(error, ct, "source.query");
        }
    }

    public async Task<ViewConfig> ResolveAsync(ViewConfig view, CancellationToken ct = default)
    {
        try
        {
            var resolved = await _locations.ResolveAsync(
                view.Source.Settings,
                view.Source.Query,
                view.Source.InputSnapshot,
                ct);
            return view with { Source = view.Source with { InputSnapshot = resolved.Snapshot } };
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Parquet input resolution was cancelled.", error, ct);
            throw Classify(error, ct, "source.query");
        }
    }

    public async Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default)
    {
        try
        {
            var resolved = await ResolveForViewAsync(view, ct);
            return await WithSessionAsync(resolved, async (db, token) =>
            {
                await MaterializeAsync(db, resolved, DatasetSource.FromSnapshot(view.Source), view.BakeFilters, token);
                using var command = db.Connection.CreateCommand();
                command.CommandText = "SELECT min(x), max(x), min(y), max(y), count(*), count(DISTINCT (x, y)) FROM canonical";
                using var reader = await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                long count = reader.GetInt64(4);
                if (count == 0) return new SourceBounds(default, 0, 0);
                double Number(int index) => Convert.ToDouble(reader.GetValue(index), CultureInfo.InvariantCulture);
                return new SourceBounds(
                    new Bbox(Number(0), Number(2), Number(1), Number(3)),
                    count,
                    reader.GetInt64(5));
            }, ct);
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("The Parquet probe was cancelled.", error, ct);
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
        string fullDestination = Path.GetFullPath(destinationParquet);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (directory is null) throw new ArgumentException("A destination directory is required.", nameof(destinationParquet));
        Directory.CreateDirectory(directory);
        string partial = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.partial");
        try
        {
            var resolved = await ResolveForViewAsync(view, ct);
            await WithSessionAsync(resolved, async (db, token) =>
            {
                var dataset = DatasetSource.FromSnapshot(view.Source);
                await MaterializeAsync(db, resolved, dataset, view.BakeFilters, token);
                using var command = db.Connection.CreateCommand();
                command.CommandText = $"COPY (SELECT * FROM canonical ORDER BY {CanonicalOrder(bounds, dataset)}) TO {Literal(partial)} (FORMAT PARQUET)";
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, ct);
            File.Move(partial, fullDestination, overwrite: true);
            progress?.Report(new SourceExtractProgress(new FileInfo(fullDestination).Length, Completed: true));
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Parquet extraction was cancelled.", error, ct);
            throw Classify(error, ct, "source.query");
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private async Task<ResolvedParquetSource> ResolveForViewAsync(ViewConfig view, CancellationToken ct) =>
        await _locations.ResolveAsync(view.Source.Settings, view.Source.Query, view.Source.InputSnapshot, ct);

    private async Task<T> WithSessionAsync<T>(
        ResolvedParquetSource source,
        Func<DuckDbSession, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        string root = Path.GetFullPath(_options.WorkDirectory);
        Directory.CreateDirectory(root);
        string work = Path.Combine(root, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            using var db = DuckDbSession.OnDisk(work, _resources);
            ConfigureRemote(db, source);
            return await operation(db, ct);
        }
        finally
        {
            string resolvedWork = Path.GetFullPath(work);
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolvedWork.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Directory.Exists(resolvedWork))
                Directory.Delete(resolvedWork, recursive: true);
        }
    }

    private static void ConfigureRemote(DuckDbSession db, ResolvedParquetSource source)
    {
        if (source.Kind != ParquetProviderKind.S3) return;
        var provider = source.Provider;
        var attributes = new List<string> { "TYPE s3" };
        if (!string.IsNullOrWhiteSpace(provider.AccessKeyId)) attributes.Add($"KEY_ID {Literal(provider.AccessKeyId)}");
        if (!string.IsNullOrWhiteSpace(provider.SecretAccessKey)) attributes.Add($"SECRET {Literal(provider.SecretAccessKey)}");
        if (!string.IsNullOrWhiteSpace(provider.SessionToken)) attributes.Add($"SESSION_TOKEN {Literal(provider.SessionToken)}");
        if (!string.IsNullOrWhiteSpace(provider.Region)) attributes.Add($"REGION {Literal(provider.Region)}");
        if (!string.IsNullOrWhiteSpace(provider.Endpoint)) attributes.Add($"ENDPOINT {Literal(provider.Endpoint)}");
        attributes.Add($"USE_SSL {(provider.UseSsl ? "true" : "false")}");
        if (provider.UrlStyle) attributes.Add("URL_STYLE 'path'");
        if (attributes.Count > 2 || !string.IsNullOrWhiteSpace(provider.AccessKeyId))
            db.Exec($"CREATE TEMP SECRET colossus_parquet ({string.Join(", ", attributes)})");
    }

    private static async Task<long> MetadataRowsAsync(
        DuckDbSession db,
        ResolvedParquetSource source,
        CancellationToken ct)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = $"SELECT coalesce(sum(num_rows), 0) FROM parquet_file_metadata({LocationList(source.ReadLocations)})";
        object? value = await command.ExecuteScalarAsync(ct);
        return value is BigInteger bigInteger
            ? checked((long)bigInteger)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> MaterializeAsync(
        DuckDbSession db,
        ResolvedParquetSource source,
        DatasetSource dataset,
        IReadOnlyList<string>? filters,
        CancellationToken ct)
    {
        var projection = Projection(dataset);
        db.Exec($"CREATE TABLE canonical ({CanonicalSchema(dataset)})");
        using var command = db.Connection.CreateCommand();
        command.CommandText = projection.SelectFrom(ReadParquet(source), filters);
        using var reader = await command.ExecuteReaderAsync(ct);
        using var appender = db.Connection.CreateAppender("canonical");
        long count = 0;
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var canonical = Canonicalize(reader, projection, dataset);
            var row = appender.CreateRow();
            row.AppendValue(canonical.Geometry.X).AppendValue(canonical.Geometry.Y);
            if (canonical.Geometry.Coordinates is not null)
                row.AppendValue(canonical.Geometry.Coordinates).AppendValue(canonical.Geometry.PartOffsets!);
            foreach (object? value in canonical.Channels) Append(row, value);
            row.EndRow();
            count++;
        }
        appender.Close();
        return count;
    }

    private static CanonicalRow Canonicalize(
        DbDataReader reader,
        ParquetProjection projection,
        DatasetSource dataset)
    {
        object? Value(string column)
        {
            int index = projection.Indexes[column];
            return reader.IsDBNull(index) ? null : reader.GetValue(index);
        }
        var geometry = CanonicalGeometryNormalizer.Normalize(dataset.Geometry, Value);
        object?[] channels = dataset.Channels.Select(channel => ConvertChannel(Value(channel.Column), channel)).ToArray();
        return new CanonicalRow(geometry, channels);
    }

    private static object? ConvertChannel(object? value, ChannelSpec channel)
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
            string message = $"Channel '{channel.Name}' cannot be converted to {channel.Type.ToString().ToLowerInvariant()}.";
            throw new SourceOperationException(
                SourceErrorKind.Type,
                message,
                new Dictionary<string, string[]> { [$"source.channels.{channel.Name}"] = [message] },
                innerException: error);
        }
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

    private static ParquetProjection Projection(DatasetSource dataset)
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
        string select = string.Join(", ", columns.Select((column, index) => $"{Identifier(column)} AS __p{index}"));
        return new ParquetProjection(select, indexes);
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

    private static IReadOnlyList<SourcePreviewColumn> PreviewColumns(
        DatasetSource dataset,
        ParquetProjection projection,
        IReadOnlyList<bool> sourceNullable)
    {
        var columns = new List<SourcePreviewColumn>
        {
            new(TileSchema.X, "FLOAT", false, "geometry"),
            new(TileSchema.Y, "FLOAT", false, "geometry"),
        };
        if (dataset.Geometry.Kind is GeometryKind.Quadkey or GeometryKind.Wkt)
        {
            columns.Add(new SourcePreviewColumn(TileSchema.Geometry, "FLOAT[]", false, "geometry"));
            columns.Add(new SourcePreviewColumn(TileSchema.PartOffsets, "INTEGER[]", false, "geometry"));
        }
        columns.AddRange(dataset.Channels.Select(channel => new SourcePreviewColumn(
            channel.Name,
            DuckType(channel.Type),
            channel.Type != ChannelType.Dict && sourceNullable[projection.Indexes[channel.Column]],
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
        for (int index = 0; index < dataset.Channels.Count; index++)
            values[dataset.Channels[index].Name] = Format(row.Channels[index]);
        return values;
    }

    private static long EstimateBytes(CanonicalRow row, DatasetSource dataset)
    {
        long bytes = 32;
        if (row.Geometry.Coordinates is not null)
            bytes += row.Geometry.Coordinates.LongLength * sizeof(float) + row.Geometry.PartOffsets!.LongLength * sizeof(int);
        for (int index = 0; index < dataset.Channels.Count; index++)
            bytes += Encoding.UTF8.GetByteCount(Format(row.Channels[index]) ?? "null") + dataset.Channels[index].Name.Length;
        return bytes;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static Bbox Bounds(IEnumerable<CanonicalGeometry> geometries)
    {
        CanonicalGeometry[] values = geometries.ToArray();
        return new Bbox(values.Min(value => value.X), values.Min(value => value.Y), values.Max(value => value.X), values.Max(value => value.Y));
    }

    private static bool Geographic(GeometrySpec geometry) => geometry.Kind != GeometryKind.Xy || geometry.Geographic;

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

    private static string ReadParquet(ResolvedParquetSource source) =>
        $"read_parquet({LocationList(source.ReadLocations)}, union_by_name = true, hive_partitioning = true)";

    private static string LocationList(IReadOnlyList<string> locations) =>
        "[" + string.Join(", ", locations.Select(Literal)) + "]";

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

    private static SourceOperationException Classify(Exception error, CancellationToken ct, string field)
    {
        if (error is SourceOperationException source) return source;
        if (error is OperationCanceledException && ct.IsCancellationRequested)
            return new SourceOperationException(SourceErrorKind.Cancellation, "The Parquet source operation was cancelled.", innerException: error);
        if (error is HttpRequestException)
            return new SourceOperationException(
                SourceErrorKind.Connectivity,
                "The remote Parquet input could not be reached.",
                new Dictionary<string, string[]> { [field] = [error.Message] },
                transient: true,
                innerException: error);
        if (error is DuckDBException duck)
        {
            bool missing = ContainsAny(duck.Message, "No files found", "does not exist", "HTTP Error 404");
            bool type = ContainsAny(duck.Message, "Binder Error", "not found in FROM clause", "Conversion Error", "Could not convert");
            var kind = missing ? SourceErrorKind.Configuration : type ? SourceErrorKind.Type : SourceErrorKind.Query;
            string target = type ? "source.mapping" : field;
            return new SourceOperationException(
                kind,
                duck.Message,
                new Dictionary<string, string[]> { [target] = [duck.Message] },
                innerException: duck);
        }
        return new SourceOperationException(
            SourceErrorKind.Query,
            error.Message,
            new Dictionary<string, string[]> { [field] = [error.Message] },
            innerException: error);
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed record CanonicalRow(CanonicalGeometry Geometry, object?[] Channels);

    private sealed record ParquetProjection(string Select, IReadOnlyDictionary<string, int> Indexes)
    {
        public string SelectFrom(string from, IReadOnlyList<string>? filters = null) =>
            $"SELECT {Select} FROM {from}" +
            (filters is { Count: > 0 } ? " WHERE " + string.Join(" AND ", filters.Select(filter => $"({filter})")) : "");
    }
}
