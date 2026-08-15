using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Hangfire;

namespace Colossus.Server.Scheduling;

public sealed class HangfireBakeScheduler(
    IBackgroundJobClient jobs,
    IRecurringJobManager recurring) : IBakeJobScheduler
{
    public Task<string?> EnqueueAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult<string?>(jobs.Enqueue<HangfireBakeJob>(job =>
            job.ExecuteAsync(runId, CancellationToken.None)));

    public Task CancelAsync(string backgroundJobId, CancellationToken ct = default)
    {
        _ = jobs.Delete(backgroundJobId);
        return Task.CompletedTask;
    }

    public Task ReconcileAsync(BakePolicy policy, CancellationToken ct = default)
    {
        string recurringId = RecurringId(policy.MapId);
        if (!policy.Enabled)
        {
            recurring.RemoveIfExists(recurringId);
            return Task.CompletedTask;
        }

        policy.Validate();
        recurring.AddOrUpdate<HangfireRecurringBakeJob>(
            recurringId,
            job => job.DispatchAsync(policy.MapId, CancellationToken.None),
            Cron.Minutely(),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.FindSystemTimeZoneById(policy.TimeZoneId) });
        return Task.CompletedTask;
    }

    private static string RecurringId(Guid mapId) => $"map-bake-{mapId:N}";
}

public sealed class DisabledBakeJobScheduler : IBakeJobScheduler
{
    public Task<string?> EnqueueAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task CancelAsync(string backgroundJobId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReconcileAsync(BakePolicy policy, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class HangfireBakeJob(BakeRunExecutor executor)
{
    [Queue("bakes")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ExecuteAsync(Guid runId, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(runId, cancellationToken);
}

public sealed class HangfireRecurringBakeJob(BakeSchedulingService scheduling)
{
    [Queue("bakes")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task DispatchAsync(Guid mapId, CancellationToken cancellationToken) =>
        scheduling.DispatchDueAsync(mapId, cancellationToken);
}

public sealed class BakeScheduleReconciler(BakeSchedulingService scheduling) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => scheduling.ReconcileAllAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
