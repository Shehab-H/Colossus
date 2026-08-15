using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Catalog;

namespace Colossus.Application.Catalog;

public sealed class BakeRunExecutionOptions
{
    public const string Section = "BakeExecution";
    public int MaxTransientAttempts { get; set; } = 3;
    public int CancellationPollMilliseconds { get; set; } = 500;
    public int ProgressUpdateMinMilliseconds { get; set; } = 500;
    public int[] RetryDelaySeconds { get; set; } = [2, 10];

    public void Normalize()
    {
        MaxTransientAttempts = Math.Max(1, MaxTransientAttempts);
        CancellationPollMilliseconds = Math.Clamp(CancellationPollMilliseconds, 100, 30_000);
        ProgressUpdateMinMilliseconds = Math.Clamp(ProgressUpdateMinMilliseconds, 100, 30_000);
        RetryDelaySeconds = RetryDelaySeconds.Length == 0 ? [1] : RetryDelaySeconds.Select(value => Math.Max(0, value)).ToArray();
    }
}

/// <summary>Executes one immutable run snapshot and owns every durable state transition.</summary>
public sealed class BakeRunExecutor(
    IBakeRunRepository runs,
    IReleaseRepository releases,
    IBakePolicyRepository policies,
    IMapBakeLock mapLocks,
    IBakePipeline pipeline,
    BakeRunExecutionOptions options,
    TimeProvider clock)
{
    public async Task ExecuteAsync(Guid runId, CancellationToken workerCancellation = default)
    {
        while (true)
        {
            var run = await RequiredRunAsync(runId, workerCancellation);
            if (run.IsTerminal) return;
            if (run.CancellationRequested)
            {
                await CancelledAsync(run, "Cancellation was requested before execution.", workerCancellation);
                return;
            }

            var policy = await policies.FindAsync(run.MapId, workerCancellation);
            IAsyncDisposable? lease = policy?.AllowOverlap == true
                ? NoopLease.Instance
                : await mapLocks.TryAcquireAsync(run.MapId, workerCancellation);
            if (lease is null)
            {
                // A recovered queue entry can briefly duplicate the same durable job. The owner has already
                // moved this run to Running, so the duplicate is an idempotent no-op rather than a cancellation.
                if ((await RequiredRunAsync(run.Id, workerCancellation)).Status == BakeRunStatus.Running) return;
                await CancelledAsync(run, "Another bake for this map is already active.", workerCancellation, "overlap");
                return;
            }

            await using (lease)
            {
                run = await runs.UpdateAsync(run with
                {
                    Status = BakeRunStatus.Running,
                    Stage = BakeRunStage.Probing,
                    Progress = 0.03,
                    StatusMessage = "Starting bake.",
                    StartedAt = run.StartedAt ?? clock.GetUtcNow(),
                    UpdatedAt = clock.GetUtcNow(),
                    Attempt = run.Attempt + 1,
                    Error = null,
                    FailureCategory = null,
                    FailureDetails = null,
                }, workerCancellation);

                using var requestedCancellation = CancellationTokenSource.CreateLinkedTokenSource(workerCancellation);
                using var stopMonitor = new CancellationTokenSource();
                var monitor = MonitorCancellationAsync(run.Id, requestedCancellation, stopMonitor.Token);
                try
                {
                    var progress = new BoundedBakeProgress(
                        this,
                        run.Id,
                        clock,
                        TimeSpan.FromMilliseconds(options.ProgressUpdateMinMilliseconds),
                        requestedCancellation.Token);
                    var outcome = await pipeline.BakeAsync(run.Definition, progress, requestedCancellation.Token);

                    await PersistProgressAsync(run.Id,
                        new BakeProgress(BakeStage.Cleanup, $"Cleaning staging files for {run.Definition.Id}."),
                        CancellationToken.None);
                    pipeline.Cleanup(run.Definition.Id);

                    long bytes = outcome.Version is null ? 0 : pipeline.VersionSize(outcome.ViewId, outcome.Version);
                    CatalogRelease? release = null;
                    if (outcome.Version is not null)
                    {
                        release = await releases.FindForRunAsync(run.Id, CancellationToken.None)
                            ?? await releases.CreateAsync(new CatalogRelease(
                                Guid.NewGuid(), run.MapId, run.Id, outcome.Version,
                                $"tiles/{outcome.ViewId}/{outcome.Version}", clock.GetUtcNow(), bytes),
                                CancellationToken.None);
                    }

                    var completed = await RequiredRunAsync(run.Id, CancellationToken.None);
                    _ = await runs.UpdateAsync(completed with
                    {
                        Status = BakeRunStatus.Succeeded,
                        Stage = BakeRunStage.Succeeded,
                        Progress = 1,
                        StatusMessage = outcome.IsEmpty ? "Bake completed with no source rows." : "Bake and publication completed.",
                        SourceRows = outcome.SourceRows,
                        TileCount = outcome.TileCount,
                        ArtifactBytes = bytes,
                        ReleaseId = release?.Id,
                        CompletedAt = clock.GetUtcNow(),
                        UpdatedAt = clock.GetUtcNow(),
                        Error = null,
                    }, CancellationToken.None);
                    return;
                }
                catch (SourceOperationException error) when (IsTransient(error) && run.Attempt < options.MaxTransientAttempts)
                {
                    SafeCleanup(run.Definition.Id);
                    var current = await RequiredRunAsync(run.Id, CancellationToken.None);
                    _ = await runs.UpdateAsync(current with
                    {
                        Status = BakeRunStatus.Queued,
                        Stage = BakeRunStage.Queued,
                        Progress = 0,
                        StatusMessage = $"Transient source failure; retry {run.Attempt + 1} of {options.MaxTransientAttempts} is pending.",
                        FailureCategory = error.Kind.ToString(),
                        FailureDetails = FailureDetails(error),
                        Error = error.Message,
                        UpdatedAt = clock.GetUtcNow(),
                    }, CancellationToken.None);
                    await Task.Delay(RetryDelay(run.Attempt), clock, workerCancellation);
                }
                catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
                {
                    SafeCleanup(run.Definition.Id);
                    var current = await RequiredRunAsync(run.Id, CancellationToken.None);
                    if (current.CancellationRequested)
                    {
                        await CancelledAsync(current, "Bake cancelled by request.", CancellationToken.None);
                        return;
                    }

                    _ = await runs.UpdateAsync(current with
                    {
                        Status = BakeRunStatus.Queued,
                        Stage = BakeRunStage.Queued,
                        Progress = 0,
                        StatusMessage = "Worker stopped; the persistent scheduler will resume this job.",
                        UpdatedAt = clock.GetUtcNow(),
                    }, CancellationToken.None);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    SafeCleanup(run.Definition.Id);
                    await CancelledAsync(await RequiredRunAsync(run.Id, CancellationToken.None),
                        "Bake cancelled by request.", CancellationToken.None);
                    return;
                }
                catch (Exception error)
                {
                    SafeCleanup(run.Definition.Id);
                    var current = await RequiredRunAsync(run.Id, CancellationToken.None);
                    _ = await runs.UpdateAsync(current with
                    {
                        Status = BakeRunStatus.Failed,
                        Stage = BakeRunStage.Failed,
                        StatusMessage = "Bake failed.",
                        Error = error.Message,
                        FailureCategory = error is SourceOperationException source ? source.Kind.ToString() : error.GetType().Name,
                        FailureDetails = error is SourceOperationException sourceError ? FailureDetails(sourceError) : null,
                        CompletedAt = clock.GetUtcNow(),
                        UpdatedAt = clock.GetUtcNow(),
                    }, CancellationToken.None);
                    return;
                }
                finally
                {
                    stopMonitor.Cancel();
                    try { await monitor; }
                    catch (OperationCanceledException) { }
                    catch { /* Progress polling must not override the durable run outcome. */ }
                }
            }
        }
    }

    private async Task MonitorCancellationAsync(
        Guid runId,
        CancellationTokenSource target,
        CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(options.CancellationPollMilliseconds), clock, stop);
            var run = await runs.FindAsync(runId, stop);
            if (run?.CancellationRequested == true)
            {
                target.Cancel();
                return;
            }
        }
    }

    private async Task PersistProgressAsync(Guid runId, BakeProgress value, CancellationToken ct)
    {
        var run = await RequiredRunAsync(runId, ct);
        _ = await runs.UpdateAsync(run with
        {
            Stage = Stage(value.Stage),
            Progress = Progress(value.Stage),
            StatusMessage = value.Message,
            UpdatedAt = clock.GetUtcNow(),
        }, ct);
    }

    private async Task CancelledAsync(
        BakeRun run,
        string message,
        CancellationToken ct,
        string category = "cancelled") =>
        _ = await runs.UpdateAsync(run with
        {
            Status = BakeRunStatus.Cancelled,
            Stage = BakeRunStage.Cancelled,
            StatusMessage = message,
            Error = message,
            FailureCategory = category,
            CompletedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow(),
        }, ct);

    private async Task<BakeRun> RequiredRunAsync(Guid runId, CancellationToken ct) =>
        await runs.FindAsync(runId, ct) ?? throw new CatalogNotFoundException($"Unknown bake run '{runId}'.");

    private void SafeCleanup(string viewId)
    {
        try { pipeline.Cleanup(viewId); }
        catch { /* Preserve the primary failure. Item 09 owns stale-workspace scavenging. */ }
    }

    private TimeSpan RetryDelay(int completedAttempt) =>
        TimeSpan.FromSeconds(options.RetryDelaySeconds[Math.Min(completedAttempt - 1, options.RetryDelaySeconds.Length - 1)]);

    private static bool IsTransient(SourceOperationException error) =>
        error.Transient || error.Kind is SourceErrorKind.Connectivity or SourceErrorKind.TransientFailure;

    private static string? FailureDetails(SourceOperationException error) => error.FieldErrors.Count == 0
        ? null
        : string.Join("; ", error.FieldErrors.SelectMany(field => field.Value.Select(value => $"{field.Key}: {value}")));

    private static BakeRunStage Stage(BakeStage stage) => stage switch
    {
        BakeStage.Probing => BakeRunStage.Probing,
        BakeStage.Extracting => BakeRunStage.Extracting,
        BakeStage.Tiling => BakeRunStage.Tiling,
        BakeStage.Verifying => BakeRunStage.Verifying,
        BakeStage.Publishing => BakeRunStage.Publishing,
        BakeStage.Cleanup => BakeRunStage.Cleanup,
        BakeStage.Completed => BakeRunStage.Cleanup,
        _ => BakeRunStage.Probing,
    };

    private static double Progress(BakeStage stage) => stage switch
    {
        BakeStage.Probing => 0.08,
        BakeStage.Extracting => 0.3,
        BakeStage.Tiling => 0.62,
        BakeStage.Verifying => 0.82,
        BakeStage.Publishing => 0.92,
        BakeStage.Cleanup => 0.97,
        BakeStage.Completed => 0.96,
        _ => 0.03,
    };

    private sealed class BoundedBakeProgress(
        BakeRunExecutor owner,
        Guid runId,
        TimeProvider time,
        TimeSpan minInterval,
        CancellationToken ct) : IProgress<BakeProgress>
    {
        private readonly object _gate = new();
        private BakeStage? _lastStage;
        private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

        public void Report(BakeProgress value)
        {
            lock (_gate)
            {
                DateTimeOffset now = time.GetUtcNow();
                if (_lastStage == value.Stage && now - _lastWrite < minInterval)
                    return;
                owner.PersistProgressAsync(runId, value, ct).GetAwaiter().GetResult();
                _lastStage = value.Stage;
                _lastWrite = now;
            }
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static NoopLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
