using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Reduction;

public interface IReductionCatalog
{
    IReductionStrategy Resolve(ReductionKind kind);
}
