using Colossus.Domain.Baking;
using Colossus.Domain.Model;

namespace Colossus.Application;

public sealed record FidelityReport(
    string ViewId, bool Passed, long LeafRows, long TotalPoints,
    int Leaves, int Internal, int OverBudget, string? Message)
{
    public static FidelityReport Mismatch(string viewId, TileId tile, long rows, long expected) =>
        new(viewId, false, 0, 0, 0, 0, 0, $"tile {tile.RelativePath} has {rows} rows, manifest says {expected}");
}

/// <summary>Re-reads the baked tiles and asserts the no-simplification invariant: leaf rows sum to the
/// source total (nothing dropped or duplicated) and every internal sample stays within budget.</summary>
public sealed class VerifyFidelityUseCase(IViewCatalog views, IBakeStore store, ITileReader tiles)
{
    public async Task<IReadOnlyList<FidelityReport>> VerifyAllAsync(CancellationToken ct = default)
    {
        var reports = new List<FidelityReport>();
        foreach (var view in views.All())
            if (await VerifyAsync(view.Id, ct) is { } report)
                reports.Add(report);
        return reports;
    }

    public async Task<FidelityReport?> VerifyAsync(string viewId, CancellationToken ct = default)
    {
        if (!store.TryReadLatestVersion(viewId, out var version))
            return null;
        if (await store.ReadManifestAsync(viewId, version, ct) is not { } manifest)
            return null;

        long leafRows = 0;
        int overBudget = 0;
        foreach (var tile in manifest.Tiles)
        {
            long rows = tiles.RowCount(store.TilePath(viewId, version, tile.Id));
            if (rows != tile.Count)
                return FidelityReport.Mismatch(viewId, tile.Id, rows, tile.Count);
            if (tile.IsLeaf) leafRows += rows;
            else if (rows > manifest.TilePointBudget) overBudget++;
        }

        bool passed = leafRows == manifest.TotalPoints && overBudget == 0;
        return new FidelityReport(viewId, passed, leafRows, manifest.TotalPoints,
            manifest.Tiles.Count(t => t.IsLeaf), manifest.Tiles.Count(t => !t.IsLeaf), overBudget, null);
    }
}
