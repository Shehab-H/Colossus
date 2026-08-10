using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Baking;

public interface IViewCatalog
{
    IReadOnlyList<ViewConfig> All();
    ViewConfig Get(string id);
}
