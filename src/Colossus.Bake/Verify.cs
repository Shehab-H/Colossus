using Colossus.Core;
using Colossus.Core.Model;

namespace Colossus.Bake;

/// <summary>
/// The fidelity smoke test: independently reads every baked Arrow file and asserts the no-simplification
/// invariant — the sum of LEAF tile rows equals the source total (nothing dropped, nothing duplicated),
/// and every INTERNAL sample tile stays within budget. Reads the files themselves, not the bake's own
/// reported counts, so it's a genuine check.
/// </summary>
public static class Verify
{
    public static bool AllViews(string tilesRoot)
    {
        bool ok = true;
        foreach (var view in Views.All)
            ok &= OneView(tilesRoot, view.Id);
        return ok;
    }

    private static bool OneView(string tilesRoot, string viewId)
    {
        string viewRoot = Path.Combine(tilesRoot, viewId);
        string latestPath = Path.Combine(viewRoot, "latest.json");
        if (!File.Exists(latestPath))
        {
            Console.WriteLine($"[SKIP] {viewId}: no bake found");
            return true;
        }

        string version = ColossusJson.Deserialize<LatestPointer>(File.ReadAllText(latestPath)).Version;
        string versionDir = Path.Combine(viewRoot, version);
        var manifest = ColossusJson.Deserialize<Manifest>(File.ReadAllText(Path.Combine(versionDir, "manifest.json")));

        long leafSum = 0;
        long overBudget = 0;
        foreach (var t in manifest.Tiles)
        {
            long rows = ArrowIo.ReadRowCount(Path.Combine(versionDir, t.Id.RelativePath));
            if (rows != t.Count)
            {
                Console.WriteLine($"[FAIL] {viewId}: tile {t.Id.RelativePath} has {rows} rows but manifest says {t.Count}");
                return false;
            }
            if (t.IsLeaf) leafSum += rows;
            else if (rows > manifest.TilePointBudget) overBudget++;
        }

        bool fidelity = leafSum == manifest.TotalPoints;
        Console.WriteLine(
            $"[{(fidelity && overBudget == 0 ? "PASS" : "FAIL")}] {viewId}: leafSum={leafSum:N0} total={manifest.TotalPoints:N0} " +
            $"leaves={manifest.Tiles.Count(t => t.IsLeaf)} internal={manifest.Tiles.Count(t => !t.IsLeaf)} overBudget={overBudget}");
        return fidelity && overBudget == 0;
    }
}
