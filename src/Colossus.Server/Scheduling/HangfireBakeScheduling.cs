using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Catalog;
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

/// <summary>Evaluates durable policies once per second. The minutely Hangfire recurrence remains a
/// persistence/restart fallback; the MySQL schedule lock prevents two hosts from dispatching one due map.</summary>
public sealed class BakeScheduleTicker(
    IBakePolicyRepository policies,
    BakeSchedulingService scheduling,
    ILogger<BakeScheduleTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            IReadOnlyList<BakePolicy> savedPolicies;
            try { savedPolicies = await policies.ListAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogError(error, "Failed to read bake policies for due-time evaluation.");
                continue;
            }

            foreach (var policy in savedPolicies.Where(policy => policy.Enabled))
            {
                try { _ = await scheduling.DispatchDueAsync(policy.MapId, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogError(error, "Failed to evaluate bake schedule for map {MapId}.", policy.MapId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
