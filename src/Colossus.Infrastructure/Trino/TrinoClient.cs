using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Colossus.Infrastructure.Trino;

public sealed record TrinoConnection(
    Uri Coordinator,
    string User,
    string Catalog,
    string Schema,
    IReadOnlyDictionary<string, string> SessionProperties,
    IReadOnlyDictionary<string, string> Headers,
    int MaxRetries,
    long MaxPageBytes);

public sealed record TrinoColumn(string Name, string Type);
public sealed record TrinoQueryStats(string State, long ProcessedRows, long ProcessedBytes);
public sealed record TrinoPage(
    string QueryId,
    IReadOnlyList<TrinoColumn> Columns,
    IReadOnlyList<JsonElement[]> Rows,
    TrinoQueryStats Stats,
    IReadOnlyList<string> Warnings,
    long PayloadBytes);

public sealed class TrinoProtocolException(
    string message,
    string? queryId = null,
    string? errorName = null,
    string? errorType = null,
    int? errorCode = null,
    bool transient = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string? QueryId { get; } = queryId;
    public string? ErrorName { get; } = errorName;
    public string? ErrorType { get; } = errorType;
    public int? ErrorCode { get; } = errorCode;
    public bool Transient { get; } = transient;
}

public sealed class TrinoResponseTooLargeException(long limit)
    : Exception($"A Trino result page exceeded the {limit:N0}-byte limit.");

/// <summary>Direct-protocol Trino transport. It owns pagination, response-header session state,
/// bounded transient retries, and best-effort cancellation of unfinished server queries.</summary>
public sealed class TrinoClient(HttpClient http)
{
    private static readonly HashSet<string> ForwardedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Trino-Set-Catalog", "X-Trino-Set-Schema", "X-Trino-Set-Session",
        "X-Trino-Clear-Session", "X-Trino-Started-Transaction-Id", "X-Trino-Clear-Transaction-Id",
    };

    public async IAsyncEnumerable<TrinoPage> QueryAsync(
        TrinoConnection connection,
        string sql,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        Uri? next = new(connection.Coordinator, "/v1/statement");
        bool first = true;
        bool completed = false;
        var state = new TrinoSessionState(connection);
        try
        {
            while (next is not null)
            {
                using var request = new HttpRequestMessage(first ? HttpMethod.Post : HttpMethod.Get, next);
                if (first)
                {
                    request.Content = new StringContent(sql, Encoding.UTF8, "text/plain");
                }
                ApplyHeaders(request, state);

                using var response = await SendWithRetryAsync(request, connection.MaxRetries, ct);
                CaptureHeaders(response, state);
                byte[] payload = await ReadBoundedAsync(response.Content, connection.MaxPageBytes, ct);
                var parsed = Parse(payload);
                if (parsed.Error is not null) throw parsed.Error;

                next = parsed.NextUri;
                first = false;
                yield return new TrinoPage(
                    parsed.QueryId,
                    parsed.Columns,
                    parsed.Rows,
                    parsed.Stats,
                    parsed.Warnings,
                    payload.LongLength);
            }
            completed = true;
        }
        finally
        {
            if (!completed && next is not null)
                await CancelAsync(next, connection, state);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage request,
        int maxRetries,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpRequestMessage current = attempt == 0 ? request : await CloneAsync(request, ct);
            try
            {
                var response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return response;
                bool transient = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                    || (int)response.StatusCode == 429;
                if (!transient || attempt >= maxRetries)
                {
                    string body = await response.Content.ReadAsStringAsync(ct);
                    response.Dispose();
                    throw new TrinoProtocolException(
                        $"Trino HTTP {(int)response.StatusCode}: {body}",
                        transient: transient);
                }
                TimeSpan delay = RetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            finally
            {
                if (attempt > 0) current.Dispose();
            }
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
        {
            byte[] content = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(content);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return Min(delta, TimeSpan.FromSeconds(5));
        if (response.Headers.RetryAfter?.Date is { } date) return Min(date - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        return TimeSpan.FromMilliseconds(Math.Min(1000, 75 * (1 << Math.Min(attempt, 4))));
    }

    private static TimeSpan Min(TimeSpan value, TimeSpan ceiling) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value < ceiling ? value : ceiling;

    private static void ApplyHeaders(HttpRequestMessage request, TrinoSessionState state)
    {
        Add("X-Trino-User", state.User);
        Add("X-Trino-Source", "colossus");
        Add("X-Trino-Catalog", state.Catalog);
        Add("X-Trino-Schema", state.Schema);
        Add("X-Trino-Time-Zone", "UTC");
        Add("X-Trino-Client-Capabilities", "PARAMETRIC_DATETIME,NUMBER");
        if (state.SessionProperties.Count > 0)
            Add("X-Trino-Session", string.Join(',', state.SessionProperties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Encode(pair.Key)}={Encode(pair.Value)}")));
        foreach (var header in state.Headers) Add(header.Key, header.Value);
        return;

        void Add(string name, string value)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new TrinoProtocolException($"Trino request header '{name}' is invalid.");
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static void CaptureHeaders(HttpResponseMessage response, TrinoSessionState state)
    {
        foreach (var header in response.Headers.Where(header => ForwardedResponseHeaders.Contains(header.Key)))
        {
            foreach (string value in header.Value)
            {
                if (header.Key.Equals("X-Trino-Set-Catalog", StringComparison.OrdinalIgnoreCase)) state.Catalog = Decode(value);
                else if (header.Key.Equals("X-Trino-Set-Schema", StringComparison.OrdinalIgnoreCase)) state.Schema = Decode(value);
                else if (header.Key.Equals("X-Trino-Set-Session", StringComparison.OrdinalIgnoreCase)) SetPair(state.SessionProperties, value);
                else if (header.Key.Equals("X-Trino-Clear-Session", StringComparison.OrdinalIgnoreCase)) state.SessionProperties.Remove(Decode(value));
                else if (header.Key.Equals("X-Trino-Started-Transaction-Id", StringComparison.OrdinalIgnoreCase)) state.Headers["X-Trino-Transaction-Id"] = value;
                else if (header.Key.Equals("X-Trino-Clear-Transaction-Id", StringComparison.OrdinalIgnoreCase)) state.Headers.Remove("X-Trino-Transaction-Id");
            }
        }
    }

    private static void SetPair(IDictionary<string, string> values, string pair)
    {
        int separator = pair.IndexOf('=');
        if (separator > 0) values[Decode(pair[..separator].Trim())] = Decode(pair[(separator + 1)..].Trim());
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private async Task CancelAsync(Uri next, TrinoConnection connection, TrinoSessionState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, next);
            ApplyHeaders(request, state);
            using var response = await http.SendAsync(request, timeout.Token);
        }
        catch
        {
            // The local cancellation remains authoritative; server cleanup is best effort.
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > limit)
            throw new TrinoResponseTooLargeException(limit);
        await using var source = await content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (destination.Length + read > limit) throw new TrinoResponseTooLargeException(limit);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return destination.ToArray();
    }

    private static ParsedPage Parse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        string queryId = root.TryGetProperty("id", out var id) ? id.GetString() ?? "unknown" : "unknown";
        if (root.TryGetProperty("error", out var error))
        {
            string message = error.TryGetProperty("message", out var value) ? value.GetString() ?? "Trino query failed." : "Trino query failed.";
            return ParsedPage.Failed(new TrinoProtocolException(
                message,
                queryId,
                error.TryGetProperty("errorName", out value) ? value.GetString() : null,
                error.TryGetProperty("errorType", out value) ? value.GetString() : null,
                error.TryGetProperty("errorCode", out value) && value.TryGetInt32(out int code) ? code : null,
                transient: error.TryGetProperty("errorType", out value) && value.GetString() == "EXTERNAL"));
        }

        Uri? next = root.TryGetProperty("nextUri", out var nextElement) && Uri.TryCreate(nextElement.GetString(), UriKind.Absolute, out var uri) ? uri : null;
        var columns = root.TryGetProperty("columns", out var columnArray) && columnArray.ValueKind == JsonValueKind.Array
            ? columnArray.EnumerateArray().Select(column => new TrinoColumn(
                column.GetProperty("name").GetString() ?? "unknown",
                column.GetProperty("type").GetString() ?? "unknown")).ToArray()
            : [];
        var rows = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(row => row.EnumerateArray().Select(value => value.Clone()).ToArray()).ToArray()
            : [];
        var stats = root.TryGetProperty("stats", out var statsElement)
            ? new TrinoQueryStats(
                statsElement.TryGetProperty("state", out var state) ? state.GetString() ?? "UNKNOWN" : "UNKNOWN",
                Long(statsElement, "processedRows"),
                Long(statsElement, "processedBytes"))
            : new TrinoQueryStats("UNKNOWN", 0, 0);
        var warnings = root.TryGetProperty("warnings", out var warningArray) && warningArray.ValueKind == JsonValueKind.Array
            ? warningArray.EnumerateArray().Select(warning => warning.TryGetProperty("message", out var message) ? message.GetString() ?? "Trino warning" : "Trino warning").ToArray()
            : [];
        return new ParsedPage(queryId, next, columns, rows, stats, warnings, null);
    }

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out long number) ? number : 0;

    private sealed class TrinoSessionState(TrinoConnection connection)
    {
        public string User { get; } = connection.User;
        public string Catalog { get; set; } = connection.Catalog;
        public string Schema { get; set; } = connection.Schema;
        public Dictionary<string, string> SessionProperties { get; } = new(connection.SessionProperties, StringComparer.Ordinal);
        public Dictionary<string, string> Headers { get; } = new(connection.Headers, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ParsedPage(
        string QueryId,
        Uri? NextUri,
        IReadOnlyList<TrinoColumn> Columns,
        IReadOnlyList<JsonElement[]> Rows,
        TrinoQueryStats Stats,
        IReadOnlyList<string> Warnings,
        TrinoProtocolException? Error)
    {
        public static ParsedPage Failed(TrinoProtocolException error) =>
            new(error.QueryId ?? "unknown", null, [], [], new TrinoQueryStats("FAILED", 0, 0), [], error);
    }
}
