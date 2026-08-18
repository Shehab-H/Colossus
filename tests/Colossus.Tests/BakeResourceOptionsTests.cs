using Colossus.Application;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.DuckDb;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using Xunit;

namespace Colossus.Tests;

public sealed class BakeResourceOptionsTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("colossus-budget-");

    public void Dispose() => _work.Delete(recursive: true);

    [Fact]
    public void RamTarget_ProducesSmallerTilesInsteadOfRejectingTheBake()
    {
        var planner = new BakePlanner(targetTileWorkingSetBytes: 10 * 1024 * 1024);
        var probe = new SourceBounds(new Bbox(0, 0, 10, 10), 100_000, 100_000);

        var plan = planner.Plan(probe, View(), estimatedWorkingBytesPerRow: 1024);

        Assert.Equal(10 * 1024, plan.TilePointBudget);
        Assert.Equal(ReductionKind.QuadtreeLod, plan.Reduction);
    }

    [Fact]
    public void RamTarget_RejectsAnEnvelopeTooSmallToMakeProgressAtStartup()
    {
        var resources = new BakeResourceOptions { RamMb = 128 };

        var error = Assert.Throws<InvalidOperationException>(resources.Normalize);

        Assert.Contains("at least 256 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuckDb_UsesBoundedThreadsAndAnOutOfCoreMemoryTarget()
    {
        var resources = new BakeResourceOptions { CpuCores = 1, RamMb = 512 };
        resources.Normalize();

        using var db = DuckDbSession.OnDisk(_work.FullName, resources);
        Assert.Equal(1, db.Scalar("SELECT current_setting('threads')"));

        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT current_setting('memory_limit')";
        string memory = Convert.ToString(command.ExecuteScalar())!;
        Assert.EndsWith("MiB", memory, StringComparison.Ordinal);
        Assert.DoesNotContain("GiB", memory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClickHouseProbe_BoundsTheOriginalSourceQueryBeforeExtraction()
    {
        var handler = new RecordingClickHouseHandler();
        var clickHouse = new ClickHouseOptions { BaseUrl = "http://clickhouse.test:8123" };
        using var client = new ClickHouseClient(new HttpClient(handler), clickHouse);
        var adapter = new ClickHouseAdapter(
            client,
            new HttpClientFactory(handler),
            Options.Create(clickHouse),
            new BakeResourceOptions { CpuCores = 2, RamMb = 512 });

        _ = await adapter.ProbeAsync(View() with
        {
            Source = View().Source with { Adapter = "clickhouse" },
        });

        Assert.Contains("max_threads = 2", handler.Sql, StringComparison.Ordinal);
        Assert.Contains("uniqCombined64", handler.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("uniqExact", handler.Sql, StringComparison.Ordinal);
    }

    private static ViewConfig View() => new()
    {
        Id = "budgeted",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Query = "select 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
        },
    };

    private sealed class RecordingClickHouseHandler : HttpMessageHandler
    {
        public string Sql { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sql = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("0\t1\t0\t1\t10\t10\n", Encoding.UTF8),
            };
        }
    }

    private sealed class HttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
