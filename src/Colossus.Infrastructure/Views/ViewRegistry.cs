using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Serialization;

namespace Colossus.Infrastructure.Views;

/// <summary>The view catalog backed by JSON files under <c>views/</c>. Adding a view is dropping (or
/// POSTing) a file — no code, no redeploy.</summary>
public sealed class ViewRegistry(string? directory = null) : IViewCatalog
{
    private readonly string _dir = directory ?? RepoPaths.ViewsDir;

    public IReadOnlyList<ViewConfig> All()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<ViewConfig>();
        return Directory.EnumerateFiles(_dir, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(Load)
            .ToList();
    }

    public ViewConfig Get(string id)
    {
        string direct = Path.Combine(_dir, id + ".json");
        if (File.Exists(direct)) return Load(direct);

        return All().FirstOrDefault(v => v.Id == id) ?? throw new ArgumentException(
            $"Unknown view id '{id}'. Known: {string.Join(", ", All().Select(v => v.Id))}");
    }

    public string Save(ViewConfig view)
    {
        view.Validate();
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, view.Id + ".json");
        File.WriteAllText(path, ColossusJson.Serialize(view));
        return path;
    }

    private static ViewConfig Load(string path)
    {
        var view = ColossusJson.Deserialize<ViewConfig>(File.ReadAllText(path));
        view.Validate();
        return view;
    }
}
