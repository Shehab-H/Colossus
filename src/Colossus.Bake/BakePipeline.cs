using System.Globalization;
using Colossus.Core;
using Colossus.Core.Model;
using Colossus.Core.Reduction;

namespace Colossus.Bake;

/// <summary>
/// One end-to-end bake for a single view: probe the source, plan the pyramid, extract a Hilbert-sorted
/// staging Parquet from ClickHouse, run the reduction primitive, then write the manifest and flip the
/// atomic latest.json pointer. The source touches only the probe + extract — everything after is
/// source-agnostic.
/// </summary>
public static class BakePipeline
{
    private const int TilePointBudget = 250_000;

    public static async Task BakeAsync(ClickHouseClient ch, ViewDescriptor view, string tilesRoot, string stagingRoot)
    {
        Console.WriteLine($"\n=== Baking '{view.Id}' ({view.Viewport}) from {view.Source.Table} ===");

        // 1. Probe: bounds + count over the two primary dims.
        var (raw, total) = await ProbeAsync(ch, view);
        if (total == 0) { Console.WriteLine("  (empty source — skipped)"); return; }
        Bbox root = raw.ToPaddedSquare();

        // 2. Plan: budget + safety max depth. Regime is always the pyramid for M1.
        int maxZoom = PlanMaxZoom(total, TilePointBudget);
        Console.WriteLine($"  probe: {total:N0} rows, bbox=({raw.MinX:0.###},{raw.MinY:0.###})–({raw.MaxX:0.###},{raw.MaxY:0.###}), maxZoom={maxZoom}");

        // 3. Extract Hilbert-sorted staging Parquet from ClickHouse.
        string staging = Path.Combine(stagingRoot, $"{view.Id}.parquet");
        await ExtractAsync(ch, view, raw, staging);
        Console.WriteLine($"  extracted → {staging}");

        // 4. Reduce (quadtree LOD) into tiles under a fresh version dir.
        string version = "v" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        string viewRoot = Path.Combine(tilesRoot, view.Id);
        string versionDir = Path.Combine(viewRoot, version);
        var reducer = new QuadtreeLodReducer();
        var result = reducer.Reduce(new ReductionContext
        {
            StagingParquetPath = staging,
            OutputDir = versionDir,
            Root = root,
            TilePointBudget = TilePointBudget,
            MaxZoom = maxZoom,
            View = view,
        });
        int leaves = result.Tiles.Count(t => t.IsLeaf);
        Console.WriteLine($"  tiled: {result.Tiles.Count} tiles ({leaves} leaves), {result.TotalPoints:N0} points in leaves");

        // 5. Manifest, then atomically flip the pointer.
        var manifest = new Manifest
        {
            Version = version,
            View = view,
            Regime = "large",
            Root = root,
            MinZoom = 0,
            MaxZoom = maxZoom,
            TilePointBudget = TilePointBudget,
            TotalPoints = result.TotalPoints,
            Tiles = result.Tiles,
        };
        await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), ColossusJson.Serialize(manifest));
        FlipLatest(viewRoot, version);
        Console.WriteLine($"  committed {view.Id} → {version}");
    }

    private static async Task<(Bbox Raw, long Total)> ProbeAsync(ClickHouseClient ch, ViewDescriptor v)
    {
        string sql = $"""
            SELECT min({v.Source.XColumn}), max({v.Source.XColumn}),
                   min({v.Source.YColumn}), max({v.Source.YColumn}), count()
            FROM {v.Source.Table} FORMAT TabSeparated
            """;
        string[] f = (await ch.QueryTextAsync(sql)).Trim().Split('\t');
        double D(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
        long total = long.Parse(f[4], CultureInfo.InvariantCulture);
        return (new Bbox(D(0), D(2), D(1), D(3)), total);
    }

    private static int PlanMaxZoom(long total, int budget)
    {
        // Enough depth that the densest leaf can reach the budget; the adaptive recursion stops early
        // wherever a node is already under budget, so this is only a safety cap.
        double perAxis = Math.Sqrt((double)total / budget);
        int z = (int)Math.Ceiling(Math.Log2(Math.Max(perAxis, 1))) + 3;
        return Math.Clamp(z, 1, 14);
    }

    private static async Task ExtractAsync(ClickHouseClient ch, ViewDescriptor v, Bbox raw, string dest)
    {
        string valueExpr = v.Source.ValueColumn is { } vc ? $"toFloat32({vc})" : "toFloat32(0)";
        string catExpr = v.Source.CategoryColumn is { } cc ? $"toUInt8({cc})" : "toUInt8(0)";
        string order = HilbertOrder(v, raw);

        string sql = $"""
            SELECT {v.Source.XColumn} AS x, {v.Source.YColumn} AS y,
                   {valueExpr} AS value, {catExpr} AS category
            FROM {v.Source.Table}
            ORDER BY {order}
            FORMAT Parquet
            """;
        await ch.QueryToFileAsync(sql, dest);
    }

    private static string HilbertOrder(ViewDescriptor v, Bbox raw)
    {
        if (raw.SpanX <= 0 || raw.SpanY <= 0) return "rand()";
        string qx = Grid(v.Source.XColumn, raw.MinX, raw.SpanX);
        string qy = Grid(v.Source.YColumn, raw.MinY, raw.SpanY);
        return $"hilbertEncode({qx}, {qy})";

        static string Grid(string col, double min, double span) =>
            $"toUInt32(least(65535, greatest(0, ({col} - {R(min)}) / {R(span)} * 65535)))";
    }

    private static void FlipLatest(string viewRoot, string version)
    {
        string tmp = Path.Combine(viewRoot, "latest.json.tmp");
        string final = Path.Combine(viewRoot, "latest.json");
        File.WriteAllText(tmp, ColossusJson.Serialize(new LatestPointer(version)));
        File.Move(tmp, final, overwrite: true);
    }

    private static string R(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
