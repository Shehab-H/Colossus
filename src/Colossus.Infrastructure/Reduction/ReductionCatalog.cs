using Colossus.Domain.Model;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Infrastructure.Configuration;

namespace Colossus.Infrastructure.Reduction;

public sealed class ReductionCatalog(BakeResourceOptions? resources = null) : IReductionCatalog
{
    public IReductionStrategy Resolve(ReductionKind kind) => kind switch
    {
        ReductionKind.QuadtreeLod => new QuadtreeLodReducer(resources),
        ReductionKind.RawPassthrough => new RawPassthroughReducer(resources),
        ReductionKind.Aggregate => new AggregateReducer(resources),
        _ => throw new NotSupportedException($"Reduction '{kind}' is not implemented yet."),
    };
}
