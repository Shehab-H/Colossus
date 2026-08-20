using Colossus.Application;
using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Baking;

public sealed class BakePipeline(BakeViewUseCase bake, IBakeStore store, IReleaseArtifactStore artifacts) : IBakePipeline
{
    public Task<BakeOutcome> BakeAsync(
        ViewConfig snapshot,
        BakeAttempt attempt,
        IProgress<BakeProgress>? progress = null,
        CancellationToken ct = default) => bake.BakeAsync(snapshot, attempt, progress, ct);

    public void Publish(BakeOutcome outcome)
    {
        if (outcome.Version is not null)
            artifacts.PublishCurrent(outcome.ViewId, outcome.Version);
    }

    public void Cleanup(string viewId, string attemptId)
    {
        store.CleanupStaging(viewId, attemptId);
        store.CleanupWorkspace(attemptId);
    }
}
