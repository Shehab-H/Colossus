using Colossus.Application.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Infrastructure.Serialization;
using Microsoft.Extensions.Options;

namespace Colossus.Infrastructure.Catalog;

public sealed record LegacyImportResult(int Discovered, int Imported, int AlreadyPresent);

/// <summary>Non-destructive migration from authored view JSON. Files remain the rollback source; the
/// workspace-scoped map slug makes repeated startup imports idempotent.</summary>
public sealed class LegacyViewImporter(ProductCatalogService catalog, IOptions<StorageOptions> configuredOptions)
{
    public async Task<LegacyImportResult> ImportAsync(CancellationToken ct = default)
    {
        StorageOptions options = configuredOptions.Value;
        if (!Directory.Exists(options.LegacyViewsDirectory)) return new LegacyImportResult(0, 0, 0);

        string[] files = Directory.EnumerateFiles(options.LegacyViewsDirectory, "*.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var existing = (await catalog.ListMapsAsync(ct: ct))
            .Select(m => m.Draft.Slug)
            .ToHashSet(StringComparer.Ordinal);

        int imported = 0;
        foreach (string file in files)
        {
            var view = ColossusJson.Deserialize<ViewConfig>(await File.ReadAllTextAsync(file, ct));
            await catalog.ImportLegacyViewAsync(view, ct: ct);
            if (existing.Add(view.Id)) imported++;
        }

        return new LegacyImportResult(files.Length, imported, files.Length - imported);
    }
}

public sealed class DatabaseBootstrapper(LegacyViewImporter importer)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public async Task<LegacyImportResult> InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized) return new LegacyImportResult(0, 0, 0);
            var result = await importer.ImportAsync(ct);
            _initialized = true;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
