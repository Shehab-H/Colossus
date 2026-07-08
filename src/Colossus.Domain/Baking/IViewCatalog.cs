using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

public interface IViewCatalog
{
    IReadOnlyList<ViewConfig> All();
    ViewConfig Get(string id);
    string Save(ViewConfig view);
}
