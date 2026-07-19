using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Application;

public sealed record FidelityReport(
    string ViewId, bool Passed, long LeafRows, long TotalPoints, long? SourceRows,
    int Leaves, int Internal, int OverBudget, string? Message)
{
    public static FidelityReport Failed(string viewId, string message) =>
        new(viewId, false, 0, 0, null, 0, 0, 0, message);
}

/// <summary>Re-reads the baked tiles and asserts the no-simplification invariant: leaf rows sum to the
/// source total (nothing dropped or duplicated) and every internal sample stays within budget.
///
/// <para>The source total has to come from outside the bake. <c>manifest.TotalPoints</c> is *defined* as
/// the leaf sum, so checking one against the other is a tautology — it passed for months while the tiling
/// double-counted rows on tile seams. The real witnesses are the staged extract the reducer read and the
/// row count the source reported into <c>manifest.SourceRows</c>.</para></summary>
public sealed class VerifyFidelityUseCase(
    IViewCatalog views, IBakeStore store, ITileReader tiles, IStagingReader staging)
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
        // Internal tiles merged on the ~1px grid are bounded by its capacity, which may exceed the leaf budget.
        long internalCap = Math.Max(manifest.TilePointBudget, (long)TileSchema.GridPerTile * TileSchema.GridPerTile);
        // A packed bake (tile-transfer Phase 3) keeps no per-tile render file, so the row count reads through
        // the pack's geometry block — the same directory the client fetches, which is what makes this a
        // witness of the artifact actually served rather than of a staging leftover.
        var renderPack = manifest.RenderPack;
        string renderVersionDir = renderPack is null ? "" : store.VersionDirectory(viewId, version);
        string renderPackPath = renderPack is null ? "" : Path.Combine(renderVersionDir, renderPack.File);
        byte[]? renderDict = renderPack?.Dict is { } rd ? File.ReadAllBytes(Path.Combine(renderVersionDir, rd)) : null;

        foreach (var tile in manifest.Tiles)
        {
            long rows = RenderRowCount(viewId, version, tile, renderPack, renderPackPath, renderDict);
            if (rows != tile.Count)
                return FidelityReport.Failed(viewId, $"tile {tile.Id.RelativePath} has {rows} rows, manifest says {tile.Count}");
            if (tile.IsLeaf) leafRows += rows;
            else if (rows > internalCap) overBudget++;
        }

        int leaves = manifest.Tiles.Count(t => t.IsLeaf);
        var report = new FidelityReport(viewId, true, leafRows, manifest.TotalPoints, manifest.SourceRows,
            leaves, manifest.Tiles.Count - leaves, overBudget, null);

        if (Diagnose(viewId, version, manifest, leafRows, overBudget) is { } failure)
            return report with { Passed = false, Message = failure };
        return report;
    }

    /// <summary>One render tile's rows, through the pack when the bake wrote one and from the per-tile file
    /// otherwise (formats 1/2 and older bakes — the same gating the client uses). A tile missing from the
    /// pack directory is a real failure, not a fallback: the file it would fall back to no longer exists.</summary>
    private long RenderRowCount(string viewId, string version, TileMeta tile,
        RenderPack? pack, string packPath, byte[]? dict)
    {
        if (pack is null)
            return tiles.RowCount(store.TilePath(viewId, version, tile.Id));

        string key = $"{tile.Z}/{tile.X}/{tile.Y}";
        if (!pack.Entries.TryGetValue(key, out var groups) || !groups.TryGetValue(RenderPack.GeomGroup, out var geom))
            throw new InvalidOperationException($"render pack has no '{RenderPack.GeomGroup}' block for tile {key}");
        return tiles.RenderPackedRowCount(packPath, geom[0], geom[1], pack.Codec, dict);
    }

    private string? Diagnose(string viewId, string version, Manifest manifest, long leafRows, int overBudget)
    {
        if (leafRows != manifest.TotalPoints)
            return $"leaf rows {leafRows:N0} != manifest totalPoints {manifest.TotalPoints:N0}";

        string stagingPath = store.StagingPath(viewId);
        long? stagedRows = staging.Exists(stagingPath) ? staging.RowCount(stagingPath) : null;

        if (stagedRows is { } staged && manifest.SourceRows is { } source && staged != source)
            return $"staged extract has {staged:N0} rows, source reported {source:N0} — the extract lost rows";

        // Group regime: a leaf holds distinct *marks*, not source rows, so the leaf sum witnesses against
        // the grouped marks staging; the source rows are witnessed by the fact companions instead.
        // Row regime is unchanged: the leaf sum is the source rows.
        return manifest.CompanionTiles
            ? DiagnoseGroup(viewId, version, manifest, leafRows, stagingPath, stagedRows, overBudget)
            : DiagnoseRow(leafRows, stagedRows, manifest.SourceRows, overBudget);
    }

    private string? DiagnoseRow(long leafRows, long? stagedRows, long? sourceRows, int overBudget)
    {
        long? expected = stagedRows ?? sourceRows;
        if (expected is null)
            return "no staged extract and no sourceRows in the manifest — the leaf sum has no independent witness";

        if (leafRows != expected)
        {
            long delta = leafRows - expected.Value;
            string cause = delta > 0 ? "rows counted in more than one leaf" : "rows in no leaf at all";
            return $"leaf rows {leafRows:N0} != source {expected:N0} ({delta:+#;-#;0} — {cause})";
        }

        return overBudget > 0 ? $"{overBudget} internal tile(s) over the merge-grid cap" : null;
    }

    private string? DiagnoseGroup(string viewId, string version, Manifest manifest, long leafRows,
        string stagingPath, long? stagedRows, int overBudget)
    {
        // Leaf marks == distinct marks (one row per geometry in the grouped marks staging).
        string marksPath = MarksPath(stagingPath);
        if (staging.Exists(marksPath) && staging.RowCount(marksPath) is var marks && leafRows != marks)
            return $"leaf marks {leafRows:N0} != distinct marks {marks:N0} (marks staging) — grouping lost or split a mark";

        // Σ leaf-companion facts == source rows: every fact lands in exactly one leaf's companion, and the
        // reference source is already at the companion grain, so the two agree. Slab bakes witness via the
        // cnt plane / sparse nnz (SLAB-FORMAT §7); a row-form R2 bake reads each leaf block's rows; per-file
        // leaves are the pre-pack layout.
        long? source = stagedRows ?? manifest.SourceRows;
        if (source is { } src)
        {
            var pack = manifest.CompanionPack;
            string versionDir = pack is null ? "" : store.VersionDirectory(viewId, version);
            string packPath = pack is null ? "" : Path.Combine(versionDir, pack.File);
            // The slab pack's trained zstd dictionary (Work Item C), read once for the whole witness.
            byte[]? dict = pack?.Dict is { } d ? File.ReadAllBytes(Path.Combine(versionDir, d)) : null;
            long companionRows = 0;
            foreach (var tile in manifest.Tiles.Where(t => t.IsLeaf))
            {
                string key = $"{tile.Z}/{tile.X}/{tile.Y}";
                if (manifest.CompanionSlab is { } slab && pack?.PlaneEntries is { } pe && pe.TryGetValue(key, out var planes))
                {
                    long f = tiles.SlabFacts(packPath, planes, slab, pack.Codec, dict);
                    if (f < 0) { companionRows = -1; break; } // no independent slab witness
                    companionRows += f;
                }
                else if (pack is not null && pack.Entries.TryGetValue(key, out var e))
                    companionRows += tiles.PackedRowCount(packPath, e[0], e[1], pack.Codec);
                else if (CompanionPath(store.TilePath(viewId, version, tile.Id)) is var facts && tiles.Exists(facts))
                    companionRows += tiles.RowCount(facts);
            }
            if (companionRows >= 0 && companionRows != src)
                return $"companion facts {companionRows:N0} != source {src:N0} — a fact reached no leaf companion (or was double-counted)";
        }

        return overBudget > 0 ? $"{overBudget} internal tile(s) over the merge-grid cap" : null;
    }

    private static string MarksPath(string staging) => Path.ChangeExtension(staging, ".marks.parquet");
    private static string CompanionPath(string tilePath) => Path.ChangeExtension(tilePath, ".facts.arrow");
}
