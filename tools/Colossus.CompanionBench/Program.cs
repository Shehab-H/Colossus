// Companion-scale R5 re-encoder / measurement harness (STEP 0). Reads an existing bake's facts.pack and
// reports, without changing any format:
//   (a) per-leaf-tile occupancy — facts / (marks × cells) — vs a per-tile dense gate (0.5);
//   (b) at-rest bytes for whole-tile *plane* blocks vs simulated cell-run (per cell row) blocks, each under
//       gzip, zstd (high level), and zstd + a dictionary trained on the cell-run blocks (ZstdSharp/ZDICT);
//   (c) simulated interaction fetch bytes — 2 cell rows per cumulative plane per selected categorical
//       position (the R5 slice model) — for representative contexts, vs today's whole-plane plane split.
//
// It follows web/scripts/bench-companion.ts: latest.json → manifest.json, ranges each block out of
// facts.pack exactly as the worker does, and folds the real slab metadata. It is written in C# (not TS)
// because dictionary *training* (ZDICT) needs a managed zstd lib; Node's zlib exposes zstd codec but not
// the trainer. Run:  dotnet run -c Release --project tools/Colossus.CompanionBench -- mobile-coverage mobile-dominance
//
// Output: a console summary + a machine-readable JSON dump (path printed at the end) for R5-BUILD.md.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Serialization;
using Colossus.Infrastructure.Tiles;
using ZstdSharp;

const double DenseGate = 0.5;      // per-tile occupancy ≥ this ⇒ dense (matches SLAB-FORMAT §3)
const int ZstdLevel = 19;          // "high level" — the bake is a batch job (Work Item C uses a high level)
const int DictCapacity = 112 * 1024;
const int DictSampleTiles = 12;
const string IdxPlane = "@idx";    // SLAB-FORMAT §5 (SlabCompanionWriter.IdxPlane, which is internal)

var views = args.Where(a => !a.StartsWith('-')).ToArray();
// --after reads a re-baked (zstd+dict) pack's directory only — actual at-rest bytes and the worst dense
// tile's cell-run interaction fetch — with no block decompression (the "before" path reconstructs dense from
// the old sparse gzip bakes and needs to read blocks; the "after" bake is already dense with a slice
// directory, so the manifest carries the measured numbers).
bool after = args.Contains("--after");
if (views.Length == 0)
{
    Console.WriteLine("usage: dotnet run --project tools/Colossus.CompanionBench -- [--after] <viewId> [<viewId> ...]");
    return 1;
}

var report = new List<object>();
foreach (var viewId in views)
{
    try { report.Add(after ? AfterBench(viewId) : Bench(viewId)); }
    catch (Exception ex) { Console.Error.WriteLine($"[{viewId}] {ex.Message}"); }
}

string outPath = Path.Combine(Path.GetTempPath(), "companion-bench.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nJSON: {outPath}");
return 0;

// ── one view ────────────────────────────────────────────────────────────────────────────────────────
object Bench(string viewId)
{
    string viewDir = Path.Combine(RepoPaths.TilesDir, viewId);
    string version = ColossusJson.Deserialize<LatestPointer>(File.ReadAllText(Path.Combine(viewDir, "latest.json"))).Version;
    string dir = Path.Combine(viewDir, version);
    var manifest = ColossusJson.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));

    var slab = manifest.CompanionSlab ?? throw new InvalidOperationException("view has no companionSlab (row-form bake)");
    var pack = manifest.CompanionPack ?? throw new InvalidOperationException("view has no companionPack");
    if (pack.PlaneEntries is null) throw new InvalidOperationException("pack has no planeEntries (pre-R1 bake)");
    string packPath = Path.Combine(dir, pack.File);
    int cells = slab.Cells;

    Console.WriteLine($"\n═══ {viewId}  {version}  ({slab.Layout}, cells={cells}, occ={slab.Occupancy:P1}) ═══");

    // Axis geometry: the one categorical axis (equality select) and the cumulative ordered axis (range).
    var axisStrides = Strides(slab.Axes);
    int cumIdx = LastIndex(slab.Axes, a => a.Cumulative);
    int cumStride = cumIdx >= 0 ? axisStrides[cumIdx] : 1;
    int cumCard = cumIdx >= 0 ? slab.Axes[cumIdx].Cardinality : 1;
    int catCount = cells / cumCard; // categorical cross-product positions (= runs)

    var leaves = manifest.Tiles.Where(t => t.IsLeaf).Select(t => $"{t.Z}/{t.X}/{t.Y}").ToList();

    // ── (a) per-tile occupancy (cheap: read only @idx per tile) ──────────────────────────────────────
    var tiles = new List<TileStat>();
    foreach (var key in leaves)
    {
        if (!pack.PlaneEntries.TryGetValue(key, out var planeDir)) continue;
        var idx = planeDir[IdxPlane];
        var (markCount, nnz) = ReadIdxShape(packPath, idx);
        double occ = markCount > 0 && cells > 0 ? (double)nnz / ((double)markCount * cells) : 0;
        long wholeTile = pack.Entries[key][1];
        tiles.Add(new TileStat(key, markCount, nnz, occ, wholeTile, planeDir));
    }

    tiles.Sort((a, b) => b.WholeTileLen.CompareTo(a.WholeTileLen));
    var denseGated = tiles.Where(t => t.Occupancy >= DenseGate).ToList();
    long totalNnz = tiles.Sum(t => (long)t.Nnz);
    long denseNnz = denseGated.Sum(t => (long)t.Nnz);
    PrintOccupancy(tiles, denseGated, totalNnz, denseNnz);

    // ── train a zstd dictionary over cell-run blocks (Work Item C is per (view, version)) ────────────
    // Price the tiles the dense path would actually encode (occ ≥ gate); if none cross the gate on this
    // (sparse) reference data, price the densest as a clearly-labeled proxy so the dense economics — which
    // the design scenario lives on — are still measured. The interaction tile is the largest one that will
    // slice (dense-gated), not the largest overall (which may be sparse and opt out).
    bool gated = denseGated.Count > 0;
    var priceSet = gated ? denseGated : tiles.Take(Math.Min(24, tiles.Count)).ToList();
    var interactionTile = priceSet.OrderByDescending(t => t.WholeTileLen).First();
    var worst = tiles[0]; // worst single-tile region today (baseline; shown even if it stays sparse)

    byte[] dict = TrainDict(packPath, slab, priceSet, cumIdx, cumStride, cumCard, cells);
    Console.WriteLine($"  zstd dict: trained {dict.Length:N0} B over cell-run samples from {Math.Min(priceSet.Count, DictSampleTiles)} tiles");

    using var zStd = new Compressor(ZstdLevel);
    using var zDict = new Compressor(ZstdLevel);
    zDict.LoadDictionary(dict);

    // ── (b) at-rest bytes + (c) worst-tile interaction, over the dense-priced set ────────────────────
    var partials = slab.Partials;
    var colorPlanes = ColorMeasurePlanes(manifest); // the archetypal recolor fetch (R5 target)

    var atRest = new AtRest();
    DenseTilePrice? interactionPrice = null;
    int priced = 0;
    foreach (var t in priceSet)
    {
        var dense = BuildDense(packPath, slab, t, cumIdx, cumStride, cumCard, cells);
        var p = PriceDenseTile(dense, t, cells, partials, colorPlanes, catCount, cumCard, cumStride, axisStrides,
            slab, zStd, zDict);
        atRest.Add(p);
        if (t.Key == interactionTile.Key) interactionPrice = p;
        priced++;
    }

    PrintAtRest(atRest, priceSet, gated);
    var interaction = PrintInteraction(interactionTile, interactionPrice!, colorPlanes, slab, cumCard, catCount, gated);

    return new
    {
        view = viewId,
        version,
        layout = slab.Layout,
        cells,
        globalOccupancy = slab.Occupancy,
        catCount,
        cumCard,
        leaves = tiles.Count,
        denseGatedTiles = denseGated.Count,
        denseGatedFactShare = totalNnz > 0 ? (double)denseNnz / totalNnz : 0,
        occupancyPercentiles = Percentiles(tiles.Select(t => t.Occupancy).ToList()),
        pricedTiles = priced,
        pricedProxy = !gated,
        dictBytes = dict.Length,
        colorMeasure = manifest.View.Encoding?.Color?.Channel,
        colorPlanes,
        atRest = atRest.ToJson(),
        worstByBytesTile = new { key = worst.Key, marks = worst.MarkCount, occupancy = worst.Occupancy, shippedWholeTile = worst.WholeTileLen },
        interactionTile = new
        {
            key = interactionTile.Key,
            marks = interactionTile.MarkCount,
            nnz = interactionTile.Nnz,
            occupancy = interactionTile.Occupancy,
            gated,
            planeSplitToday = interaction.PlaneSplitTodayBytes,
            contexts = interaction.Rows,
        },
    };
}

// ── after mode: directory-only analysis of a re-baked zstd+dict pack ───────────────────────────────────
object AfterBench(string viewId)
{
    string viewDir = Path.Combine(RepoPaths.TilesDir, viewId);
    string version = ColossusJson.Deserialize<LatestPointer>(File.ReadAllText(Path.Combine(viewDir, "latest.json"))).Version;
    string dir = Path.Combine(viewDir, version);
    var manifest = ColossusJson.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir, "manifest.json")));
    var slab = manifest.CompanionSlab ?? throw new InvalidOperationException("no companionSlab");
    var pack = manifest.CompanionPack ?? throw new InvalidOperationException("no companionPack");
    int cells = slab.Cells;
    var axisStrides = Strides(slab.Axes);
    int cumIdx = LastIndex(slab.Axes, a => a.Cumulative);
    int cumCard = cumIdx >= 0 ? slab.Axes[cumIdx].Cardinality : 1;
    int catCount = cells / cumCard;

    var leaves = manifest.Tiles.Where(t => t.IsLeaf).Select(t => $"{t.Z}/{t.X}/{t.Y}").ToList();
    var dense = leaves.Where(k => slab.LayoutOf(k) == "dense").ToList();
    long leafPackBytes = leaves.Where(k => pack.Entries.ContainsKey(k)).Sum(k => pack.Entries[k][1]);
    long dictBytes = pack.Dict is { } d && File.Exists(Path.Combine(dir, d)) ? new FileInfo(Path.Combine(dir, d)).Length : 0;

    Console.WriteLine($"\n═══ {viewId}  {version}  (codec={pack.Codec}, dict={dictBytes:N0} B) ═══");
    Console.WriteLine($"  dense-gated leaf tiles: {dense.Count}/{leaves.Count}   leaf pack bytes: {Mb(leafPackBytes)}");

    if (dense.Count == 0) return new { view = viewId, version, codec = pack.Codec, dense = 0, leafPackBytes, dictBytes };

    // Worst dense tile = the dense leaf with the largest whole-tile region.
    string worst = dense.OrderByDescending(k => pack.Entries[k][1]).First();
    var planeDir = pack.PlaneEntries![worst];
    var sliceDir = pack.SliceEntries![worst];
    var colorPlanes = ColorMeasurePlanes(manifest);
    var active = colorPlanes.Append("cnt").Distinct().Where(sliceDir.ContainsKey).ToArray(); // + cnt survival

    long planeSplitToday = colorPlanes.Where(planeDir.ContainsKey).Sum(p => planeDir[p][1]);
    Console.WriteLine($"\n  ── worst dense tile {worst}: interaction fetch (actual sliceEntries) ──");
    Console.WriteLine($"     color planes [{string.Join(", ", colorPlanes)}]   plane-split (whole planes): {Mb(planeSplitToday)}");

    var contexts = new (string Name, int Positions, int Rows)[]
    {
        ("single operator + date window (2 bins)", 1, 2),
        ("single operator + full range (cumulative from start)", 1, 1),
        ("single operator + single quarter", 1, 2),
        ("date-range window (2 bins), all operators", catCount, 2),
    };
    var rows = new List<object>();
    foreach (var (name, positions, r) in contexts)
    {
        long bytes = 0;
        foreach (var p in active)
        {
            bool scan = p.StartsWith("min__", StringComparison.Ordinal) || p.StartsWith("max__", StringComparison.Ordinal);
            int rowsNeeded = scan ? 2 : r;
            var lens = sliceDir[p];
            for (int cat = 0; cat < Math.Min(positions, catCount); cat++)
                for (int k = 0; k < Math.Min(rowsNeeded, cumCard); k++)
                {
                    int cell = cat * cumCard + (cumCard - 1 - k);
                    if (cell >= 0 && cell < lens.Length) bytes += lens[cell];
                }
        }
        double ratio = bytes > 0 ? (double)planeSplitToday / bytes : 0;
        Console.WriteLine($"     {name,-52}  {Mb(bytes),12}  {ratio,10:0.0}×");
        rows.Add(new { context = name, cellRunBytes = bytes, vsPlaneSplit = ratio });
    }

    return new
    {
        view = viewId,
        version,
        codec = pack.Codec,
        denseTiles = dense.Count,
        leafTiles = leaves.Count,
        leafPackBytes,
        dictBytes,
        worstDenseTile = worst,
        worstMarks = manifest.Tiles.First(t => $"{t.Z}/{t.X}/{t.Y}" == worst).Count,
        colorPlanes,
        planeSplitToday,
        interaction = rows,
    };
}

// ── dense construction (reconstruct cell-major cumulative planes from the sparse CSR) ──────────────────
DenseTile BuildDense(string packPath, CompanionSlab slab, TileStat t, int cumIdx, int cumStride, int cumCard, int cells)
{
    var tile = SlabCompanionReader.Read(packPath, t.PlaneDir, slab);
    int m = t.MarkCount;
    int[] cellIds = tile.CellIds ?? throw new InvalidOperationException($"tile {t.Key} has no @idx (not sparse?)");
    int[] offsets = tile.Offsets!;
    // mki per entry from the CSR offsets.
    var mki = new int[cellIds.Length];
    for (int mk = 0; mk < m; mk++)
        for (int e = offsets[mk]; e < offsets[mk + 1]; e++) mki[e] = mk;

    var planes = new Dictionary<string, float[]>(StringComparer.Ordinal);
    foreach (var p in slab.Partials)
    {
        bool minmax = p.Name.StartsWith("min__", StringComparison.Ordinal) || p.Name.StartsWith("max__", StringComparison.Ordinal);
        var plane = new float[(long)cells * m];
        if (minmax) Array.Fill(plane, float.NaN);
        // Raw values, scattered cell-major. Sparse planes are raw (non-cumulative), parallel to cellIds.
        var src = tile.FloatPlanes.TryGetValue(p.Name, out var fp) ? fp
            : tile.IntPlanes.TryGetValue(p.Name, out var ip) ? Array.ConvertAll(ip, x => (float)x)
            : throw new InvalidOperationException($"plane {p.Name} absent");
        for (int e = 0; e < cellIds.Length; e++) plane[(long)cellIds[e] * m + mki[e]] = src[e];
        if (!minmax) Cumulate(plane, m, cumStride, cumCard, cells);
        planes[p.Name] = plane;
    }
    return new DenseTile(m, planes);
}

// Prefix-sum each cell along the cumulative axis within its categorical run (mirrors SlabCompanionWriter).
void Cumulate(float[] plane, int m, int stride, int card, int cells)
{
    for (int c = 0; c < cells; c++)
    {
        if (c / stride % card == 0) continue;
        long prev = (long)(c - stride) * m, cur = (long)c * m;
        for (int k = 0; k < m; k++) plane[cur + k] += plane[prev + k];
    }
}

// ── pricing one dense tile ─────────────────────────────────────────────────────────────────────────
DenseTilePrice PriceDenseTile(DenseTile dense, TileStat t, int cells, IReadOnlyList<SlabPartial> partials,
    string[] colorPlanes, int catCount, int cumCard, int cumStride, int[] axisStrides, CompanionSlab slab,
    Compressor zStd, Compressor zDict)
{
    int m = dense.MarkCount;
    int rowBytes = m * 4; // one cell row = m elements × 4 B (f32 or i32)
    var wholePlane = new CodecBytes();
    var cellRun = new CodecBytes();
    // Per (plane, cell) zstd+dict compressed length — the interaction fetch reads specific cell rows.
    var cellDictLen = new Dictionary<string, int[]>(StringComparer.Ordinal);

    foreach (var p in partials)
    {
        float[] plane = dense.Planes[p.Name];
        byte[] planeBytes = PlaneBytes(plane, p.Name);
        wholePlane.Gzip += Gzip(planeBytes);
        wholePlane.Zstd += zStd.Wrap(planeBytes).Length;
        wholePlane.ZstdDict += zDict.Wrap(planeBytes).Length;

        var lens = new int[cells];
        var rowBuf = new byte[rowBytes];
        for (int c = 0; c < cells; c++)
        {
            CellRowBytes(plane, c, m, p.Name, rowBuf);
            cellRun.Gzip += Gzip(rowBuf);
            cellRun.Zstd += zStd.Wrap(rowBuf).Length;
            int dl = zDict.Wrap(rowBuf).Length;
            cellRun.ZstdDict += dl;
            lens[c] = dl;
        }
        cellDictLen[p.Name] = lens;
    }
    return new DenseTilePrice(t.Key, m, wholePlane, cellRun, cellDictLen);
}

// ── dictionary training ──────────────────────────────────────────────────────────────────────────────
byte[] TrainDict(string packPath, CompanionSlab slab, List<TileStat> priceSet, int cumIdx, int cumStride, int cumCard, int cells)
{
    var samples = new List<byte[]>();
    long budget = 96L * 1024 * 1024; // cap sample volume so training stays quick
    long used = 0;
    var rnd = new Random(7);
    foreach (var t in priceSet.Take(DictSampleTiles))
    {
        var dense = BuildDense(packPath, slab, t, cumIdx, cumStride, cumCard, cells);
        foreach (var p in slab.Partials)
        {
            var plane = dense.Planes[p.Name];
            int m = dense.MarkCount, rowBytes = m * 4;
            var rowBuf = new byte[rowBytes];
            for (int c = 0; c < cells; c++)
            {
                if (used + rowBytes > budget) goto done;
                CellRowBytes(plane, c, m, p.Name, rowBuf);
                samples.Add((byte[])rowBuf.Clone());
                used += rowBytes;
            }
        }
    }
done:
    if (samples.Count < 16) return DictBuilder.TrainFromBuffer(samples.Count > 0 ? samples : [new byte[8]], 4 * 1024);
    return DictBuilder.TrainFromBuffer(samples, DictCapacity);
}

// ── interaction fetch model (R5 slice) ────────────────────────────────────────────────────────────────
Interaction PrintInteraction(TileStat worst, DenseTilePrice price, string[] colorPlanes, CompanionSlab slab,
    int cumCard, int catCount, bool gated)
{
    // Today: plane split fetches the whole color-measure planes (their gzip whole-tile block).
    long planeSplitToday = 0;
    foreach (var p in colorPlanes) if (worst.PlaneDir.TryGetValue(p, out var r)) planeSplitToday += r[1];

    // R5 cell-run fetch: per cumulative plane, `rows × positions` cell-row blocks (zstd+dict), where a
    // single-select picks 1 categorical position and a date range picks rows {lo-1, hi} (≤2 rows).
    var contexts = new (string Name, int Positions, int RowsPerCumPlane)[]
    {
        ("date-range window (2 bins), all operators", catCount, 2),
        ("single operator + full range (cumulative from start)", 1, 1),
        ("single operator + date window (2 bins)", 1, 2),
        ("single operator + single quarter", 1, 2),
    };

    var rows = new List<object>();
    string tag = gated ? "worst dense-gated tile" : "densest tile (proxy — none crosses the gate, would stay sparse)";
    Console.WriteLine($"\n  ── {tag}: interaction fetch  ({worst.Key}, {worst.MarkCount:N0} marks, occ {worst.Occupancy:P1}) ──");
    Console.WriteLine($"     color planes [{string.Join(", ", colorPlanes)}]   plane-split today (whole planes): {Mb(planeSplitToday)}");
    Console.WriteLine($"     {"context",-52}  {"cell-run fetch",14}  {"vs plane-split",14}");
    foreach (var (name, positions, rowsPerPlane) in contexts)
    {
        long bytes = 0;
        foreach (var p in colorPlanes)
        {
            if (!price.CellDictLen.TryGetValue(p, out var lens)) continue;
            bool cumulative = !(p.StartsWith("min__", StringComparison.Ordinal) || p.StartsWith("max__", StringComparison.Ordinal));
            int rowsNeeded = cumulative ? rowsPerPlane : 2; // scan planes read [lo..hi]; window ≈ 2 here
            bytes += SampleCellRunBytes(lens, slab, cumCard, catCount, positions, rowsNeeded);
        }
        double ratio = bytes > 0 ? (double)planeSplitToday / bytes : 0;
        Console.WriteLine($"     {name,-52}  {Mb(bytes),14}  {ratio,12:0.0}×");
        rows.Add(new { context = name, positions, rowsPerCumPlane = rowsPerPlane, cellRunBytes = bytes, vsPlaneSplit = ratio });
    }
    return new Interaction(planeSplitToday, rows);
}

// Sum the compressed cell-row blocks a fetch touches: `positions` categorical runs, `rowsPerPos` rows each,
// sampled from the run's bins (deterministic representative bins, not authored to the data).
long SampleCellRunBytes(int[] lens, CompanionSlab slab, int cumCard, int catCount, int positions, int rowsPerPos)
{
    long total = 0;
    int pos = Math.Min(positions, catCount);
    int rows = Math.Min(rowsPerPos, cumCard);
    for (int cat = 0; cat < pos; cat++)
        for (int r = 0; r < rows; r++)
        {
            // bin picked from the top of the run (hi ≈ last bin, lo-1 ≈ one below) — a representative window.
            int bin = cumCard - 1 - r;
            int cell = cat * cumCard + bin; // cumulative axis is innermost (stride 1)
            if (cell >= 0 && cell < lens.Length) total += lens[cell];
        }
    return total;
}

// ── printing ───────────────────────────────────────────────────────────────────────────────────────
void PrintOccupancy(List<TileStat> tiles, List<TileStat> denseGated, long totalNnz, long denseNnz)
{
    var occs = tiles.Select(t => t.Occupancy).OrderBy(x => x).ToList();
    Console.WriteLine($"  (a) occupancy over {tiles.Count} leaf tiles — dense gate {DenseGate:0.0}:");
    Console.WriteLine($"      min {occs[0]:P1}  p25 {P(occs, .25):P1}  p50 {P(occs, .50):P1}  " +
                      $"p75 {P(occs, .75):P1}  p90 {P(occs, .90):P1}  p99 {P(occs, .99):P1}  max {occs[^1]:P1}");
    Console.WriteLine($"      tiles ≥ gate: {denseGated.Count}/{tiles.Count} " +
                      $"({(tiles.Count > 0 ? (double)denseGated.Count / tiles.Count : 0):P1})   " +
                      $"facts in dense-gated tiles: {(totalNnz > 0 ? (double)denseNnz / totalNnz : 0):P1}");
}

void PrintAtRest(AtRest a, List<TileStat> priceSet, bool gated)
{
    string label = gated ? "dense-gated" : "densest-as-dense (proxy — no tile crosses the gate)";
    Console.WriteLine($"\n  (b) at-rest bytes over {priceSet.Count} {label} tiles — whole-plane vs cell-run blocks:");
    Console.WriteLine($"      {"",-18} {"gzip",12} {"zstd" + ZstdLevel,12} {"zstd+dict",12}");
    Console.WriteLine($"      {"whole-plane",-18} {Mb(a.Whole.Gzip),12} {Mb(a.Whole.Zstd),12} {Mb(a.Whole.ZstdDict),12}");
    Console.WriteLine($"      {"cell-run",-18} {Mb(a.Cell.Gzip),12} {Mb(a.Cell.Zstd),12} {Mb(a.Cell.ZstdDict),12}");
    double inflate = a.Whole.ZstdDict > 0 ? (double)a.Cell.ZstdDict / a.Whole.ZstdDict : 0;
    Console.WriteLine($"      cell-run at-rest inflation vs whole-plane (zstd+dict): {inflate:0.00}×  " +
                      $"(the price of sliceability; the dictionary is what keeps it near 1)");
}

// ── helpers ──────────────────────────────────────────────────────────────────────────────────────────
(int markCount, int nnz) ReadIdxShape(string packPath, long[] idx)
{
    using var stream = CompanionPackWriter.ReadBlock(packPath, idx[0], idx[1]);
    using var reader = new Apache.Arrow.Ipc.ArrowStreamReader(stream);
    var batch = reader.ReadNextRecordBatch()!;
    var offsets = (Apache.Arrow.ListArray)batch.Column("offsets");
    int markCount = (int)offsets.GetValueLength(0);
    var cellIds = (Apache.Arrow.ListArray)batch.Column("cellIds");
    int nnz = (int)cellIds.GetValueLength(0);
    return (markCount - 1, nnz);
}

byte[] PlaneBytes(float[] plane, string name)
{
    var bytes = new byte[(long)plane.Length * 4];
    bool isInt = name == "cnt";
    for (int i = 0; i < plane.Length; i++)
    {
        int off = i * 4;
        if (isInt) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(off), (int)MathF.Round(plane[i]));
        else BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(off), plane[i]);
    }
    return bytes;
}

void CellRowBytes(float[] plane, int cell, int m, string name, byte[] into)
{
    long baseIdx = (long)cell * m;
    bool isInt = name == "cnt";
    for (int k = 0; k < m; k++)
    {
        int off = k * 4;
        if (isInt) BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(off), (int)MathF.Round(plane[baseIdx + k]));
        else BinaryPrimitives.WriteSingleLittleEndian(into.AsSpan(off), plane[baseIdx + k]);
    }
}

int Gzip(byte[] data)
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
    return (int)ms.Length;
}

int[] Strides(IReadOnlyList<SlabAxis> axes)
{
    var s = new int[axes.Count];
    int stride = 1;
    for (int i = axes.Count - 1; i >= 0; i--) { s[i] = stride; stride *= axes[i].Cardinality; }
    return s;
}

int LastIndex(IReadOnlyList<SlabAxis> axes, Func<SlabAxis, bool> pred)
{
    for (int i = axes.Count - 1; i >= 0; i--) if (pred(axes[i])) return i;
    return -1;
}

// The color measure's partial planes (the archetypal single-measure recolor R5 shrinks); falls back to the
// first measure. Uses the real MeasureParser/MeasurePartials so the plane set matches the client's fold.
string[] ColorMeasurePlanes(Manifest m)
{
    var measures = m.View.Measures ?? [];
    string? colorCh = m.View.Encoding?.Color?.Channel;
    var chosen = measures.FirstOrDefault(x => x.Name == colorCh) ?? measures.FirstOrDefault();
    if (chosen is null) return [];
    var expr = MeasureParser.Parse(chosen.Expr);
    return MeasurePartials.For([expr]).Select(p => p.Name).ToArray();
}

object Percentiles(List<double> xs)
{
    xs.Sort();
    return new { min = xs[0], p25 = P(xs, .25), p50 = P(xs, .50), p75 = P(xs, .75), p90 = P(xs, .90), p99 = P(xs, .99), max = xs[^1] };
}

double P(List<double> sorted, double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
string Mb(long b) => (b / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";

// ── records ──────────────────────────────────────────────────────────────────────────────────────────
sealed record TileStat(string Key, int MarkCount, int Nnz, double Occupancy, long WholeTileLen,
    IReadOnlyDictionary<string, long[]> PlaneDir);
sealed record DenseTile(int MarkCount, Dictionary<string, float[]> Planes);
sealed class CodecBytes { public long Gzip; public long Zstd; public long ZstdDict; }
sealed record DenseTilePrice(string Key, int MarkCount, CodecBytes WholePlane, CodecBytes CellRun,
    Dictionary<string, int[]> CellDictLen);
sealed record Interaction(long PlaneSplitTodayBytes, List<object> Rows);

sealed class AtRest
{
    public CodecBytes Whole { get; } = new();
    public CodecBytes Cell { get; } = new();
    public void Add(DenseTilePrice p)
    {
        Whole.Gzip += p.WholePlane.Gzip; Whole.Zstd += p.WholePlane.Zstd; Whole.ZstdDict += p.WholePlane.ZstdDict;
        Cell.Gzip += p.CellRun.Gzip; Cell.Zstd += p.CellRun.Zstd; Cell.ZstdDict += p.CellRun.ZstdDict;
    }
    public object ToJson() => new
    {
        wholePlane = new { gzip = Whole.Gzip, zstd = Whole.Zstd, zstdDict = Whole.ZstdDict },
        cellRun = new { gzip = Cell.Gzip, zstd = Cell.Zstd, zstdDict = Cell.ZstdDict },
    };
}
