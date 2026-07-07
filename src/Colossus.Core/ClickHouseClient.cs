using System.Net.Http.Headers;
using System.Text;

namespace Colossus.Core;

/// <summary>
/// Thin wrapper over the ClickHouse HTTP interface (port 8123). Shared by Seed (DDL + INSERT…SELECT)
/// and Bake (metadata probe + FORMAT Parquet extract). No native driver — plain HTTP is enough and
/// keeps the dependency surface tiny.
/// </summary>
public sealed class ClickHouseClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ClickHouseClient(string? baseUrl = null, string? user = null, string? password = null)
    {
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("COLOSSUS_CH_URL") ?? "http://localhost:8123").TrimEnd('/');
        user ??= Environment.GetEnvironmentVariable("COLOSSUS_CH_USER") ?? "colossus";
        password ??= Environment.GetEnvironmentVariable("COLOSSUS_CH_PASSWORD") ?? "colossus";

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// <summary>Runs a statement (DDL, INSERT…SELECT). Throws with the CH error body on failure.</summary>
    public async Task ExecuteAsync(string sql, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(_baseUrl + "/", new StringContent(sql, Encoding.UTF8), ct);
        await ThrowIfError(resp, ct);
    }

    /// <summary>Runs a query and returns the response body as text (use with a FORMAT that suits parsing).</summary>
    public async Task<string> QueryTextAsync(string sql, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync(_baseUrl + "/", new StringContent(sql, Encoding.UTF8), ct);
        await ThrowIfError(resp, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Streams a query result (e.g. FORMAT Parquet) straight to a file.</summary>
    public async Task QueryToFileAsync(string sql, string destPath, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/")
        {
            Content = new StringContent(sql, Encoding.UTF8),
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfError(resp, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        await resp.Content.CopyToAsync(fs, ct);
    }

    /// <summary>Polls until the server answers, or throws after the timeout.</summary>
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

    private static async Task ThrowIfError(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string body = await resp.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException($"ClickHouse {(int)resp.StatusCode}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
