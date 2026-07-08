using Colossus.Domain.Sources;
using Colossus.Infrastructure.ClickHouse;

namespace Colossus.Infrastructure.Sources;

public sealed class SourceAdapterCatalog(ClickHouseClient clickHouse) : ISourceAdapterCatalog
{
    public ISourceAdapter Resolve(string adapter) => adapter.ToLowerInvariant() switch
    {
        "clickhouse" => new ClickHouseAdapter(clickHouse),
        _ => throw new NotSupportedException($"No source adapter '{adapter}' (known: clickhouse)"),
    };
}
