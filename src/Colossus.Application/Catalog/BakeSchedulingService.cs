using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Domain.Catalog;

namespace Colossus.Application.Catalog;

/// <summary>Coordinates durable catalog runs with the replaceable background-job adapter.</summary>
public sealed class BakeSchedulingService(
    ProductCatalogService catalog,
    IBakePolicyRepository policies,
    IBakeRunRepository runs,
    IBakeJobScheduler scheduler,
    IBakeScheduleLock scheduleLocks,
    TimeProvider clock)
{
    public async Task<BakeRun> RunNowAsync(Guid mapId, CancellationToken ct = default)
    {
        await using var lease = await AcquireScheduleLeaseAsync(mapId, ct);
        return await QueueLatestUnderLeaseAsync(mapId, ct);
    }

    private async Task<BakeRun> QueueLatestUnderLeaseAsync(Guid mapId, CancellationToken ct)
    {
        CatalogMap map = await catalog.GetMapAsync(mapId, ct);
        IReadOnlyList<BakeRun> active = await runs.ListActiveForMapAsync(mapId, ct);
        BakeRun? existing = active.FirstOrDefault(run =>
            run.Status == BakeRunStatus.Queued && run.DraftRevision == map.Revision && !run.CancellationRequested);
        if (existing is not null)
        {
            if (existing.BackgroundJobId is null)
            {
                string? recoveredJobId = await scheduler.EnqueueAsync(existing.Id, ct);
                if (recoveredJobId is not null)
                    existing = await runs.AttachBackgroundJobAsync(existing.Id, recoveredJobId, ct);
            }
            return existing;
        }

        foreach (BakeRun pending in active.Where(run =>
                     run.Status == BakeRunStatus.Queued && run.DraftRevision < map.Revision))
        {
            BakeRun superseded = await runs.SupersedeQueuedAsync(
                pending.Id, map.Revision, clock.GetUtcNow(), ct);
            if (superseded.Status == BakeRunStatus.Cancelled && pending.BackgroundJobId is { Length: > 0 } pendingJobId)
                await scheduler.CancelAsync(pendingJobId, ct);
        }

        var run = await catalog.CaptureBakeRunAsync(mapId, ct);
        string? jobId = await scheduler.EnqueueAsync(run.Id, ct);
        return jobId is null ? run : await runs.AttachBackgroundJobAsync(run.Id, jobId, ct);
    }

    private async Task<IAsyncDisposable> AcquireScheduleLeaseAsync(Guid mapId, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (await scheduleLocks.TryAcquireAsync(mapId, ct) is { } lease) return lease;
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
        throw new CatalogConflictException("Another bake request for this map is being scheduled. Retry shortly.");
    }

    public async Task<BakePolicy> SetPolicyAsync(
        BakePolicy policy,
        long? expectedRevision = null,
        CancellationToken ct = default)
    {
        var saved = await catalog.SetBakePolicyAsync(policy, expectedRevision, ct);
        await scheduler.ReconcileAsync(saved, ct);
        return saved;
    }

    public async Task<BakeRun> CancelAsync(Guid mapId, Guid runId, CancellationToken ct = default)
    {
        _ = await catalog.GetMapAsync(mapId, ct);
        var existing = await runs.FindAsync(runId, ct)
            ?? throw new CatalogNotFoundException($"Unknown bake run '{runId}'.");
        if (existing.MapId != mapId) throw new CatalogRelationshipException("Bake run belongs to another map.");

        var cancelled = await runs.RequestCancellationAsync(runId, clock.GetUtcNow(), ct);
        if (cancelled.BackgroundJobId is { Length: > 0 } jobId)
            await scheduler.CancelAsync(jobId, ct);
        return cancelled;
    }

    /// <summary>Called by the persistent minutely recurring trigger. Missed intervals coalesce to one run.</summary>
    public async Task<BakeRun?> DispatchDueAsync(Guid mapId, CancellationToken ct = default)
    {
        await using var scheduleLease = await scheduleLocks.TryAcquireAsync(mapId, ct);
        if (scheduleLease is null) return null;

        var policy = await policies.FindAsync(mapId, ct);
        if (policy is not { Enabled: true, Interval: { } interval }) return null;

        var history = await runs.GetOverviewForMapAsync(mapId, ct);
        if (history.HasActive) return null;
        DateTimeOffset dueAt = (history.Latest?.CreatedAt ?? policy.UpdatedAt) + interval;
        return clock.GetUtcNow() < dueAt ? null : await QueueLatestUnderLeaseAsync(mapId, ct);
    }

    public async Task ReconcileAllAsync(CancellationToken ct = default)
    {
        foreach (var policy in await policies.ListAsync(ct))
            await scheduler.ReconcileAsync(policy, ct);

        // A process can stop between catalog capture and queue enqueue. Only that narrow orphan case
        // is recovered here; jobs with an ID are already owned by the persistent scheduler.
        foreach (var run in await runs.ListIncompleteAsync(ct))
        {
            if (run.BackgroundJobId is not null) continue;
            string? jobId = await scheduler.EnqueueAsync(run.Id, ct);
            if (jobId is not null) _ = await runs.AttachBackgroundJobAsync(run.Id, jobId, ct);
        }
    }
}
