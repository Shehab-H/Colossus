using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Colossus.Application.Abstractions.Sources;
using Colossus.Infrastructure.Configuration;

namespace Colossus.Infrastructure.ClickHouse;

public sealed class ClickHouseRequestException(HttpStatusCode statusCode, string responseBody)
    : Exception($"ClickHouse {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}

public sealed class ClickHouseResponseTooLargeException(long maxBytes)
    : Exception($"ClickHouse response exceeded the {maxBytes:N0}-byte limit.");

public sealed class ClickHouseIncompleteResponseException(string detail)
    : Exception("ClickHouse ended the extraction before producing a complete Parquet response.")
{
    public string Detail { get; } = detail;
}

/// <summary>Typed transport over ClickHouse's HTTP query interface for text, JSON, and streamed
/// Parquet responses.</summary>
public sealed class ClickHouseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ClickHouseClient(ClickHouseOptions options)
        : this(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, options)
    {
    }

    public ClickHouseClient(HttpClient http, ClickHouseOptions options)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(30);
        _baseUrl = options.BaseUrl.TrimEnd('/');
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.User}:{options.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(_baseUrl + "/", new StringContent(sql, Encoding.UTF8), ct);
        await ThrowIfError(resp, ct);
    }

    public Task<string> QueryTextAsync(string sql, CancellationToken ct = default) =>
        QueryTextAsync(sql, maxBytes: 16 * 1024 * 1024, ct);

    public async Task<string> QueryTextAsync(string sql, long maxBytes, CancellationToken ct = default)
    {
        using var request = QueryRequest(sql);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfError(response, ct);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (destination.Length + read > maxBytes)
                throw new ClickHouseResponseTooLargeException(maxBytes);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    public async Task QueryToFileAsync(
        string sql,
        string destinationPath,
        IProgress<SourceExtractProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var request = QueryRequest(sql);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfError(response, ct);

        string destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + $".partial-{Guid.NewGuid():N}";
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, ct);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(new SourceExtractProgress(written));
            }
            await file.FlushAsync(ct);
            file.Close();
            string? incomplete = IncompleteParquetDetail(partial);
            if (incomplete is not null)
                throw new ClickHouseIncompleteResponseException(incomplete);
            File.Move(partial, destination, overwrite: true);
            progress?.Report(new SourceExtractProgress(written, Completed: true));
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await ExecuteAsync("SELECT 1", ct);
                return;
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    private HttpRequestMessage QueryRequest(string sql) => new(HttpMethod.Post, _baseUrl + "/")
    {
        Content = new StringContent(sql, Encoding.UTF8),
    };

    private static string? IncompleteParquetDetail(string path)
    {
        const int magicLength = 4;
        const int maxTailBytes = 64 * 1024;
        ReadOnlySpan<byte> magic = "PAR1"u8;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length < magicLength * 2)
            return $"ClickHouse returned only {file.Length:N0} bytes; a Parquet file requires both PAR1 markers.";

        Span<byte> marker = stackalloc byte[magicLength];
        file.ReadExactly(marker);
        bool hasHeader = marker.SequenceEqual(magic);
        file.Position = file.Length - magicLength;
        file.ReadExactly(marker);
        if (hasHeader && marker.SequenceEqual(magic)) return null;

        int tailLength = checked((int)Math.Min(file.Length, maxTailBytes));
        byte[] tail = new byte[tailLength];
        file.Position = file.Length - tailLength;
        file.ReadExactly(tail);
        string decoded = Encoding.UTF8.GetString(tail);
        int exceptionStart = decoded.LastIndexOf("Code: ", StringComparison.Ordinal);
        if (exceptionStart >= 0)
        {
            string serverError = new(decoded[exceptionStart..]
                .Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
                .ToArray());
            return serverError.Trim();
        }

        string missing = hasHeader ? "footer" : "header and footer";
        return $"ClickHouse returned {file.Length:N0} bytes without the required Parquet {missing} marker.";
    }

    private static async Task ThrowIfError(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(ct);
        throw new ClickHouseRequestException(response.StatusCode, body);
    }

    public void Dispose() => _http.Dispose();
}
