using Colossus.Domain.Model;

namespace Colossus.Domain.Reduction;

public interface IReductionCatalog
{
    IReductionStrategy Resolve(ReductionKind kind);
}
