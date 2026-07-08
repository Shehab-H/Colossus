using Colossus.Domain.Model;
using Colossus.Domain.Reduction;

namespace Colossus.Infrastructure.Reduction;

public sealed class ReductionCatalog : IReductionCatalog
{
    public IReductionStrategy Resolve(ReductionKind kind) => kind switch
    {
        ReductionKind.QuadtreeLod => new QuadtreeLodReducer(),
        ReductionKind.RawPassthrough => new RawPassthroughReducer(),
        ReductionKind.Aggregate => new AggregateReducer(),
        _ => throw new NotSupportedException($"Reduction '{kind}' is not implemented yet."),
    };
}
