using Colossus.Application;
using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Abstractions.Sources;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Catalog;
using Colossus.Infrastructure.Configuration;
using Xunit;

namespace Colossus.Tests;

public sealed class BakeSchedulingTests : IDisposable
{
    private readonly List<MySqlTestDatabase> _databases = [];

    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
    }

    [Fact]
    public async Task RunNow_ReconciliationAndDueDispatch_AreApplicationOwnedAndCoalesce()
    {
        var database = NewDatabase();
        var store = new MySqlDatabase(database.ConnectionString, new DatabaseOptions());
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var catalog = new ProductCatalogService(store, store, store, store, store, store, clock);
        var queue = new RecordingScheduler();
        var scheduling = new BakeSchedulingService(catalog, store, store, queue, clock);
        var map = await CreateMapAsync(catalog);

        var first = await scheduling.RunNowAsync(map.Id);
        _ = await catalog.UpdateMapAsync(map.Id, map.Draft with { Title = "Changed after capture" }, map.Revision);
        var policy = await scheduling.SetPolicyAsync(new BakePolicy(
            map.Id, true, TimeSpan.FromMinutes(1), "UTC", false, 3, 0, default));

        Assert.Equal("job-1", first.BackgroundJobId);
        Assert.Equal(map.Revision, first.DraftRevision);
        Assert.Equal("Scheduled map", first.Definition.Title);
        Assert.Equal(policy, Assert.Single(queue.Reconciled));
        Assert.Null(await scheduling.DispatchDueAsync(map.Id));

        var cancelled = await scheduling.CancelAsync(map.Id, first.Id);
        Assert.Equal(BakeRunStatus.Cancelled, cancelled.Status);
        Assert.Equal(["job-1"], queue.Cancelled);

        clock.Advance(TimeSpan.FromMinutes(2));
        var due = await scheduling.DispatchDueAsync(map.Id);
        Assert.NotNull(due);
        Assert.Equal("Changed after capture", due!.Definition.Title);
        Assert.Equal("job-2", due.BackgroundJobId);
        Assert.Null(await scheduling.DispatchDueAsync(map.Id));
    }

    [Fact]
    public async Task Executor_PersistsProgressMetricsReleaseAndSuccess()
    {
        var store = new RunStore(Run());
        var pipeline = new ScriptedPipeline(async (attempt, progress, ct) =>
        {
            progress?.Report(new BakeProgress(BakeStage.Probing, "Probing source."));
            progress?.Report(new BakeProgress(BakeStage.Extracting, "Extracting rows."));
            progress?.Report(new BakeProgress(BakeStage.Tiling, "Writing tiles."));
            progress?.Report(new BakeProgress(BakeStage.Verifying, "Verifying output."));
            progress?.Report(new BakeProgress(BakeStage.Publishing, "Publishing output."));
            await Task.Yield();
            return new BakeOutcome("scheduled-map", "v1", 120, 8, 4, 120, new Bbox(0, 0, 1, 1), 3);
        });
        var executor = Executor(store, pipeline, new FakeMapLock());

        await executor.ExecuteAsync(store.Run.Id);

        Assert.Equal(BakeRunStatus.Succeeded, store.Run.Status);
        Assert.Equal(BakeRunStage.Succeeded, store.Run.Stage);
        Assert.Equal(1, store.Run.Progress);
        Assert.Equal(120, store.Run.SourceRows);
        Assert.Equal(8, store.Run.TileCount);
        Assert.Equal(4096, store.Run.ArtifactBytes);
        Assert.NotNull(store.Run.ReleaseId);
        Assert.Equal(1, store.Run.Attempt);
        Assert.Single(store.Releases);
        Assert.True(pipeline.CleanupCalls > 0);
    }

    [Fact]
    public async Task Executor_RetriesOnlyTransientFailuresWithinBound()
    {
        var store = new RunStore(Run());
        var pipeline = new ScriptedPipeline((attempt, progress, ct) =>
        {
            if (attempt < 3)
                throw new SourceOperationException(
                    SourceErrorKind.TransientFailure, "Warehouse is temporarily unavailable.", transient: true);
            return Task.FromResult(new BakeOutcome(
                "scheduled-map", "v3", 10, 1, 1, 10, new Bbox(0, 0, 1, 1), 0));
        });

        await Executor(store, pipeline, new FakeMapLock()).ExecuteAsync(store.Run.Id);

        Assert.Equal(BakeRunStatus.Succeeded, store.Run.Status);
        Assert.Equal(3, store.Run.Attempt);
        Assert.Equal(3, pipeline.Calls);
    }

    [Fact]
    public async Task Executor_FailsPermanentErrorsWithoutRetryOrRelease()
    {
        var store = new RunStore(Run());
        var pipeline = new ScriptedPipeline((attempt, progress, ct) =>
            throw new SourceOperationException(
                SourceErrorKind.Configuration,
                "Dataset configuration is invalid.",
                new Dictionary<string, string[]> { ["source.query"] = ["Column 'x' was not found."] }));

        await Executor(store, pipeline, new FakeMapLock()).ExecuteAsync(store.Run.Id);

        Assert.Equal(BakeRunStatus.Failed, store.Run.Status);
        Assert.Equal(1, store.Run.Attempt);
        Assert.Equal(1, pipeline.Calls);
        Assert.Equal("Configuration", store.Run.FailureCategory);
        Assert.Contains("source.query", store.Run.FailureDetails);
        Assert.Empty(store.Releases);
        Assert.Null(store.Run.ReleaseId);
    }

    [Fact]
    public async Task Executor_RejectsOverlapBeforePipelineStarts()
    {
        var store = new RunStore(Run());
        var pipeline = new ScriptedPipeline((attempt, progress, ct) =>
            Task.FromResult(BakeOutcome.Empty("scheduled-map")));

        await Executor(store, pipeline, new FakeMapLock(available: false)).ExecuteAsync(store.Run.Id);

        Assert.Equal(BakeRunStatus.Cancelled, store.Run.Status);
        Assert.Equal("overlap", store.Run.FailureCategory);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task Executor_ObservesDurableCancellationDuringActiveWork()
    {
        var store = new RunStore(Run());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ScriptedPipeline(async (attempt, progress, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return BakeOutcome.Empty("scheduled-map");
        });
        var executor = Executor(store, pipeline, new FakeMapLock());

        var execution = executor.ExecuteAsync(store.Run.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = await store.RequestCancellationAsync(store.Run.Id, DateTimeOffset.UtcNow);
        await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(BakeRunStatus.Cancelled, store.Run.Status);
        Assert.True(store.Run.CancellationRequested);
        Assert.Null(store.Run.ReleaseId);
        Assert.Empty(store.Releases);
    }

    [Fact]
    public async Task MySqlAdvisoryLock_IsExclusiveAndReleasesWithItsLease()
    {
        var database = NewDatabase();
        var mapLock = new MySqlMapBakeLock(database.ConnectionString);
        Guid mapId = Guid.NewGuid();

        var first = await mapLock.TryAcquireAsync(mapId);
        var overlapping = await mapLock.TryAcquireAsync(mapId);
        Assert.NotNull(first);
        Assert.Null(overlapping);

        await first!.DisposeAsync();
        var afterRelease = await mapLock.TryAcquireAsync(mapId);
        Assert.NotNull(afterRelease);
        await afterRelease!.DisposeAsync();
    }

    private MySqlTestDatabase NewDatabase()
    {
        var database = new MySqlTestDatabase();
        _databases.Add(database);
        return database;
    }

    private static BakeRunExecutor Executor(RunStore store, IBakePipeline pipeline, IMapBakeLock mapLock)
    {
        var options = new BakeRunExecutionOptions
        {
            MaxTransientAttempts = 3,
            CancellationPollMilliseconds = 100,
            RetryDelaySeconds = [0],
        };
        options.Normalize();
        return new BakeRunExecutor(store, store, store, mapLock, pipeline, options, TimeProvider.System);
    }

    private static async Task<CatalogMap> CreateMapAsync(ProductCatalogService catalog)
    {
        var connection = await catalog.CreateConnectionAsync(new ConnectionDraft
        {
            Slug = "scheduled-source",
            Name = "Scheduled source",
            Adapter = "fixture",
        });
        var dataset = await catalog.CreateDatasetAsync(new DatasetDraft
        {
            Slug = "scheduled-dataset",
            Name = "Scheduled dataset",
            ConnectionId = connection.Id,
            Source = new DatasetSource
            {
                Query = "SELECT x, y FROM points",
                Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            },
        });
        return await catalog.CreateMapAsync(new MapDraft
        {
            Slug = "scheduled-map",
            Title = "Scheduled map",
            DatasetId = dataset.Id,
            Viewport = Viewport.Geo,
            Mark = Mark.Point,
        });
    }

    private static BakeRun Run() => new(
        Guid.NewGuid(), Guid.NewGuid(), 1, Snapshot(), BakeRunStatus.Queued,
        new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));

    private static ViewConfig Snapshot() => new()
    {
        Id = "scheduled-map",
        Title = "Scheduled map",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Adapter = "fixture",
            Query = "SELECT x, y FROM points",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
        },
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }

    private sealed class RecordingScheduler : IBakeJobScheduler
    {
        private int _next;
        public List<BakePolicy> Reconciled { get; } = [];
        public List<string> Cancelled { get; } = [];

        public Task<string?> EnqueueAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult<string?>($"job-{++_next}");

        public Task CancelAsync(string backgroundJobId, CancellationToken ct = default)
        {
            Cancelled.Add(backgroundJobId);
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(BakePolicy policy, CancellationToken ct = default)
        {
            Reconciled.Add(policy);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMapLock(bool available = true) : IMapBakeLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid mapId, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(available ? new Lease() : null);

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedPipeline(
        Func<int, IProgress<BakeProgress>?, CancellationToken, Task<BakeOutcome>> execute) : IBakePipeline
    {
        public int Calls { get; private set; }
        public int CleanupCalls { get; private set; }

        public Task<BakeOutcome> BakeAsync(
            ViewConfig snapshot, IProgress<BakeProgress>? progress = null, CancellationToken ct = default) =>
            execute(++Calls, progress, ct);

        public long VersionSize(string viewId, string version) => 4096;
        public void Cleanup(string viewId) => CleanupCalls++;
    }

    private sealed class RunStore(BakeRun run) : IBakeRunRepository, IReleaseRepository, IBakePolicyRepository
    {
        private readonly object _gate = new();
        private BakeRun _run = run;
        public BakeRun Run { get { lock (_gate) return _run; } }
        public List<CatalogRelease> Releases { get; } = [];

        public Task<BakeRun> CreateAsync(BakeRun value, CancellationToken ct = default)
        {
            lock (_gate) _run = value;
            return Task.FromResult(value);
        }

        public Task<BakeRun?> FindAsync(Guid id, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<BakeRun?>(_run.Id == id ? _run : null);
        }

        public Task<IReadOnlyList<BakeRun>> ListForMapAsync(Guid mapId, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<BakeRun>>(_run.MapId == mapId ? [_run] : []);
        }

        public Task<IReadOnlyList<BakeRun>> ListIncompleteAsync(CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<BakeRun>>(_run.IsTerminal ? [] : [_run]);
        }

        public Task<BakeRun> UpdateAsync(BakeRun value, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _run = value with { CancellationRequested = _run.CancellationRequested || value.CancellationRequested };
                return Task.FromResult(_run);
            }
        }

        public Task<BakeRun> RequestCancellationAsync(
            Guid id, DateTimeOffset requestedAt, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _run = _run with { CancellationRequested = true, UpdatedAt = requestedAt };
                return Task.FromResult(_run);
            }
        }

        public Task<BakeRun> AttachBackgroundJobAsync(
            Guid id, string backgroundJobId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _run = _run with { BackgroundJobId = backgroundJobId };
                return Task.FromResult(_run);
            }
        }

        public Task<CatalogRelease> CreateAsync(CatalogRelease release, CancellationToken ct = default)
        {
            lock (_gate) Releases.Add(release);
            return Task.FromResult(release);
        }

        public Task<CatalogRelease?> FindForRunAsync(Guid runId, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(Releases.FirstOrDefault(release => release.BakeRunId == runId));
        }

        Task<IReadOnlyList<CatalogRelease>> IReleaseRepository.ListForMapAsync(Guid mapId, CancellationToken ct)
        {
            lock (_gate) return Task.FromResult<IReadOnlyList<CatalogRelease>>([.. Releases.Where(release => release.MapId == mapId)]);
        }

        Task<BakePolicy?> IBakePolicyRepository.FindAsync(Guid mapId, CancellationToken ct) =>
            Task.FromResult<BakePolicy?>(null);
        Task<IReadOnlyList<BakePolicy>> IBakePolicyRepository.ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BakePolicy>>([]);
        Task<BakePolicy> IBakePolicyRepository.UpsertAsync(BakePolicy policy, long? expectedRevision, CancellationToken ct) =>
            Task.FromResult(policy);
    }
}
