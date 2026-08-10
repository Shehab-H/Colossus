using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Sources;
using Xunit;

namespace Colossus.Tests;

public class SourceAdapterCatalogTests
{
    [Fact]
    public void Resolve_UsesRegisteredAdapterIdentity_CaseInsensitively()
    {
        var adapter = new StubAdapter("trino");
        var catalog = new SourceAdapterCatalog([adapter]);

        Assert.Same(adapter, catalog.Resolve("TRINO"));
    }

    [Fact]
    public void Resolve_ReportsKnownAdapters()
    {
        var catalog = new SourceAdapterCatalog([new StubAdapter("parquet"), new StubAdapter("clickhouse")]);

        var error = Assert.Throws<NotSupportedException>(() => catalog.Resolve("missing"));
        Assert.Contains("clickhouse, parquet", error.Message);
    }

    [Fact]
    public void Constructor_RejectsDuplicateAdapterIdentities()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SourceAdapterCatalog([new StubAdapter("trino"), new StubAdapter("TRINO")]));
        Assert.Contains("registered more than once", error.Message);
    }

    private sealed class StubAdapter(string id) : ISourceAdapter
    {
        public string Id { get; } = id;
        public Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ExtractAsync(ViewConfig view, Bbox bounds, string destinationParquet,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
