using Colossus.Application.Abstractions.Sources;

namespace Colossus.Infrastructure.Sources;

public sealed class SourceAdapterCatalog : ISourceAdapterCatalog
{
    private readonly IReadOnlyDictionary<string, ISourceAdapter> _adapters;

    public SourceAdapterCatalog(IEnumerable<ISourceAdapter> adapters)
    {
        var registered = adapters.ToArray();
        var duplicate = registered.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Source adapter '{duplicate.Key}' is registered more than once.");

        _adapters = registered.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
    }

    public ISourceAdapter Resolve(string adapter) =>
        _adapters.TryGetValue(adapter, out var found)
            ? found
            : throw new NotSupportedException(
                $"No source adapter '{adapter}' (known: {string.Join(", ", _adapters.Keys.Order())})");
}
