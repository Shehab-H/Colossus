using Colossus.Application;
using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Baking;

public sealed class BakePipeline(BakeViewUseCase bake, IBakeStore store) : IBakePipeline
{
    public Task<BakeOutcome> BakeAsync(
        ViewConfig snapshot,
        IProgress<BakeProgress>? progress = null,
        CancellationToken ct = default) => bake.BakeAsync(snapshot, progress, ct);

    public long VersionSize(string viewId, string version) => store.VersionSize(viewId, version);

    public void Cleanup(string viewId) => store.CleanupStaging(viewId);
}
