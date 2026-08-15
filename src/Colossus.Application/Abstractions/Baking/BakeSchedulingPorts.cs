using Colossus.Domain.Catalog;
using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Baking;

/// <summary>Application-owned scheduling port. Infrastructure decides how jobs are persisted and executed.</summary>
public interface IBakeJobScheduler
{
    Task<string?> EnqueueAsync(Guid runId, CancellationToken ct = default);
    Task CancelAsync(string backgroundJobId, CancellationToken ct = default);
    Task ReconcileAsync(BakePolicy policy, CancellationToken ct = default);
}

/// <summary>Distributed, crash-safe exclusion for a map bake. A null lease means another worker owns it.</summary>
public interface IMapBakeLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(Guid mapId, CancellationToken ct = default);
}

/// <summary>The executable bake pipeline boundary used by durable run coordination.</summary>
public interface IBakePipeline
{
    Task<BakeOutcome> BakeAsync(
        ViewConfig snapshot,
        IProgress<BakeProgress>? progress = null,
        CancellationToken ct = default);
    long VersionSize(string viewId, string version);
    void Cleanup(string viewId);
}
