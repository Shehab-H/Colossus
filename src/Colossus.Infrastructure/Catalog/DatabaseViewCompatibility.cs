using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Catalog;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Catalog;

/// <summary>Compatibility adapter for the bake host and legacy `/api/views` URLs. Product mutations use
/// catalog commands; this seam can disappear after all callers consume map snapshots explicitly.</summary>
public sealed class DatabaseViewCompatibility(ProductCatalogService catalog) : IViewCatalog
{
    public IReadOnlyList<ViewConfig> All() => GetAllAsync().GetAwaiter().GetResult();

    public ViewConfig Get(string id)
    {
        try
        {
            return catalog.ResolveSnapshotAsync(id).GetAwaiter().GetResult();
        }
        catch (CatalogNotFoundException e)
        {
            throw new ArgumentException(e.Message, nameof(id), e);
        }
    }

    private async Task<IReadOnlyList<ViewConfig>> GetAllAsync()
    {
        var maps = await catalog.ListMapsAsync();
        var result = new List<ViewConfig>(maps.Count);
        foreach (var map in maps) result.Add(await catalog.ResolveSnapshotAsync(map.Draft.Slug));
        return result;
    }
}
