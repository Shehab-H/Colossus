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
        var run = await catalog.CaptureBakeRunAsync(mapId, ct);
        string? jobId = await scheduler.EnqueueAsync(run.Id, ct);
        return jobId is null ? run : await runs.AttachBackgroundJobAsync(run.Id, jobId, ct);
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

        var history = await runs.ListForMapAsync(mapId, ct);
        if (history.Any(run => !run.IsTerminal)) return null;
        DateTimeOffset dueAt = (history.FirstOrDefault()?.CreatedAt ?? policy.UpdatedAt) + interval;
        return clock.GetUtcNow() < dueAt ? null : await RunNowAsync(mapId, ct);
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
