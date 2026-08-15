using System.Net;
using System.Text;
using System.Text.Json;
using Colossus.Infrastructure.Trino;
using Xunit;

namespace Colossus.Tests;

public sealed class TrinoClientTests
{
    [Fact]
    public async Task DirectProtocol_RetriesTransientResponseAndFollowsEveryPage()
    {
        var handler = new ProtocolHandler { FailFirstPost = true };
        var client = new TrinoClient(new HttpClient(handler));
        var rows = new List<long>();
        var warnings = new List<string>();

        await foreach (var page in client.QueryAsync(Connection(), "SELECT value FROM fixture"))
        {
            rows.AddRange(page.Rows.Select(row => row[0].GetInt64()));
            warnings.AddRange(page.Warnings);
        }

        Assert.Equal([1, 2, 3], rows);
        Assert.Equal("fixture warning", Assert.Single(warnings));
        Assert.Equal(2, handler.Posts);
        Assert.Equal(2, handler.Gets);
        Assert.Equal("colossus", handler.InitialHeaders["X-Trino-User"]);
        Assert.Contains("query_max_execution_time=10s", handler.InitialHeaders["X-Trino-Session"]);
        Assert.Contains("fixture_property=updated+value", handler.LastGetSession);
    }

    [Fact]
    public async Task Cancellation_DeletesTheCurrentNextUri()
    {
        var handler = new ProtocolHandler { BlockSecondPage = true };
        var client = new TrinoClient(new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        await using var pages = client.QueryAsync(Connection(), "SELECT value FROM slow", cancellation.Token).GetAsyncEnumerator();
        Assert.True(await pages.MoveNextAsync());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pages.MoveNextAsync().AsTask());

        Assert.Equal(1, handler.Deletes);
    }

    [Fact]
    public async Task QueryFailure_PreservesStructuredServerEvidence()
    {
        var handler = new ProtocolHandler { ReturnQueryError = true };
        var client = new TrinoClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<TrinoProtocolException>(async () =>
        {
            await foreach (var _ in client.QueryAsync(Connection(), "SELECT missing FROM fixture")) { }
        });

        Assert.Equal("query-7", error.QueryId);
        Assert.Equal("COLUMN_NOT_FOUND", error.ErrorName);
        Assert.Equal("USER_ERROR", error.ErrorType);
        Assert.Equal(47, error.ErrorCode);
    }

    private static TrinoConnection Connection() => new(
        new Uri("http://trino.test:8080"),
        "colossus",
        "hive",
        "default",
        new Dictionary<string, string> { ["query_max_execution_time"] = "10s" },
        new Dictionary<string, string>(),
        MaxRetries: 2,
        MaxPageBytes: 1_048_576);

    private sealed class ProtocolHandler : HttpMessageHandler
    {
        public bool FailFirstPost { get; init; }
        public bool BlockSecondPage { get; init; }
        public bool ReturnQueryError { get; init; }
        public int Posts { get; private set; }
        public int Gets { get; private set; }
        public int Deletes { get; private set; }
        public Dictionary<string, string> InitialHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string LastGetSession { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Delete)
            {
                Deletes++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post)
            {
                Posts++;
                foreach (var header in request.Headers) InitialHeaders[header.Key] = string.Join(',', header.Value);
                if (FailFirstPost && Posts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (ReturnQueryError) return Json(new
                {
                    id = "query-7",
                    error = new { message = "Column 'missing' cannot be resolved", errorCode = 47, errorName = "COLUMN_NOT_FOUND", errorType = "USER_ERROR" },
                    stats = new { state = "FAILED", processedRows = 0, processedBytes = 0 },
                });
                var response = Json(new
                {
                    id = "query-1",
                    nextUri = "http://trino.test:8080/v1/statement/query-1/1",
                    columns = new[] { new { name = "value", type = "bigint" } },
                    data = new[] { new object[] { 1L } },
                    stats = new { state = "RUNNING", processedRows = 1, processedBytes = 8 },
                    warnings = new[] { new { message = "fixture warning" } },
                });
                response.Headers.TryAddWithoutValidation("X-Trino-Set-Session", "fixture_property=updated+value");
                return response;
            }

            Gets++;
            LastGetSession = request.Headers.TryGetValues("X-Trino-Session", out var sessions) ? string.Join(',', sessions) : "";
            if (BlockSecondPage) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Gets == 1
                ? Json(new
                {
                    id = "query-1",
                    nextUri = "http://trino.test:8080/v1/statement/query-1/2",
                    data = new[] { new object[] { 2L } },
                    stats = new { state = "RUNNING", processedRows = 2, processedBytes = 16 },
                })
                : Json(new
                {
                    id = "query-1",
                    data = new[] { new object[] { 3L } },
                    stats = new { state = "FINISHED", processedRows = 3, processedBytes = 24 },
                });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}
