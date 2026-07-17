using System.Buffers.Binary;
using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using DuckDB.NET.Data;
using ZstdSharp;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Writes fact companions as slabs (docs/companion-scale/SLAB-FORMAT.md) into one per-bake archive
/// (<c>facts.pack</c>). Streams the companion grain rows of each level — <c>(tx, ty, mki, cellId, partials…)</c>
/// ordered by <c>(tx, ty, mki, cellId)</c> — and, per tile, encodes either sparse CSR (Arrow blocks) or dense
/// cumulative cell-major planes (raw per-cell-row blocks — SLAB-FORMAT §4b).
///
/// <para>Codec is **zstd + a trained shared dictionary** (Work Item C): because the dictionary must exist
/// before any block is compressed but is trained on the blocks, the writer runs two passes over a scratch
/// file — pass 1 streams every block **raw** to <c>facts.pack.tmp</c> and reservoir-samples the small ones;
/// <see cref="Finish"/> trains one dictionary (ZstdSharp/ZDICT), then transcodes the raw blocks into the final
/// <c>facts.pack</c> with zstd-19+dict, rebuilding the directory with compressed offsets and writing
/// <c>facts.dict</c>. Managed only — no native bindings (SLAB-FORMAT §5).</para>
///
/// <para>The directory (<see cref="CompanionPack.Entries"/> whole-tile regions, <see cref="CompanionPack.PlaneEntries"/>
/// per-plane ranges, <see cref="CompanionPack.SliceEntries"/> dense per-cell-row lengths) rides the manifest.
/// Both leaf and internal levels are packed (R5).</para></summary>
internal sealed class SlabCompanionWriter : IDisposable
{
    public const string FileName = CompanionPackWriter.FileName; // "facts.pack"
    public const string DictFileName = "facts.dict";
    public const string Codec = "zstd";
    public const string IdxPlane = "@idx";

    private const int ZstdLevel = 19;             // high level — the bake is a batch job
    private const int DictCapacity = 112 * 1024;  // ~110 KB trained dictionary
    private const int MinSamplesForDict = 16;     // fewer than this ⇒ no dictionary (plain zstd)
    private const int MaxSampleBlock = 32 * 1024; // only small blocks are the dictionary's target
    private const int MaxSamples = 4000;          // reservoir size (bounds training memory)

    private readonly SlabPlan _plan;
    // Test support only: forces every tile's layout instead of the per-tile occupancy gate, so the shared
    // fixture can pin the dense encoder against a tile whose occupancy would otherwise select sparse. Null in
    // production (the gate decides per tile).
    private readonly bool? _layoutOverride;
    private readonly string _outputDir;
    private readonly string _tempPath;
    private readonly FileStream _pack; // pass 1: raw scratch (facts.pack.tmp); replaced by the final pack in Finish
    private readonly Dictionary<string, long[]> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, long[]>> _planeEntries = new(StringComparer.Ordinal);
    // Per-cell-row block lengths for dense tiles (R5 cell-run slicing); keyed tileKey → plane → lengths.
    private readonly Dictionary<string, IReadOnlyDictionary<string, int[]>> _sliceEntries = new(StringComparer.Ordinal);
    // Tiles whose per-tile layout (SLAB-FORMAT §3) differs from the view default (_plan.Dense) — the only
    // ones the manifest records (CompanionSlab.TileLayouts); the rest fall back to the default.
    private readonly Dictionary<string, string> _tileLayouts = new(StringComparer.Ordinal);
    // Reservoir sample of small raw blocks for dictionary training (unbiased across all levels).
    private readonly List<byte[]> _samples = new();
    private readonly Random _rng = new(1);
    private int _sampleSeen;

    /// <summary>Per-tile layout overrides discovered while writing, or null when every tile matched the view
    /// default. The reducer folds this into <see cref="CompanionSlab.TileLayouts"/>.</summary>
    public IReadOnlyDictionary<string, string>? TileLayouts => _tileLayouts.Count > 0 ? _tileLayouts : null;

    public SlabCompanionWriter(string outputDirectory, SlabPlan plan, bool? layoutOverride = null)
    {
        _plan = plan;
        _layoutOverride = layoutOverride;
        _outputDir = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
        _tempPath = Path.Combine(outputDirectory, FileName + ".tmp");
        _pack = File.Create(_tempPath);
    }

    /// <summary>Encodes one level's companion into the pack. <paramref name="markCounts"/> gives each tile's
    /// mark count (CSR <c>offsets</c> length is <c>markCount+1</c>; it also bounds the dense plane).</summary>
    public void AppendLevel(int z, DuckDBConnection conn, string sql, IReadOnlyDictionary<(long, long), long> markCounts)
    {
        int nPartials = _plan.Partials.Count;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        long tx = 0, ty = 0;
        bool open = false;
        int maxMki = -1;
        var mkis = new List<int>();
        var cellIds = new List<int>();
        var vals = new List<float>[nPartials];
        for (int i = 0; i < nPartials; i++) vals[i] = new List<float>();

        void Flush()
        {
            if (!open || cellIds.Count == 0) return;
            long marks = markCounts.TryGetValue((tx, ty), out var m) ? m : maxMki + 1;
            WriteTile($"{z}/{tx}/{ty}", (int)marks, mkis, cellIds, vals);
            mkis.Clear();
            cellIds.Clear();
            foreach (var v in vals) v.Clear();
            maxMki = -1;
        }

        // Columns: tx, ty, mki, cellId, partials… — ordered (tx, ty, mki, cellId), so entries arrive
        // mark-major with ascending cellId within a mark (the CSR order).
        while (reader.Read())
        {
            long rtx = reader.GetInt64(0), rty = reader.GetInt64(1);
            if (!open || rtx != tx || rty != ty) { Flush(); (tx, ty, open) = (rtx, rty, true); }
            int mki = reader.GetInt32(2);
            mkis.Add(mki);
            if (mki > maxMki) maxMki = mki;
            cellIds.Add((int)reader.GetInt64(3));
            for (int i = 0; i < nPartials; i++) vals[i].Add(Convert.ToSingle(reader.GetValue(4 + i)));
        }
        Flush();
    }

    private void WriteTile(string key, int markCount, List<int> mkis, List<int> cellIds, List<float>[] vals)
    {
        var planes = new Dictionary<string, long[]>(StringComparer.Ordinal);
        long start = _pack.Position;

        // The per-leaf-tile gate: this tile's own occupancy, not the view's, picks its layout.
        bool dense = _layoutOverride ?? _plan.TileDense(cellIds.Count, markCount);
        if (dense)
        {
            var slices = new Dictionary<string, int[]>(StringComparer.Ordinal);
            WriteDense(markCount, mkis, cellIds, vals, planes, slices);
            _sliceEntries[key] = slices; // dense tiles are cell-run sliceable (R5)
        }
        else WriteSparse(markCount, mkis, cellIds, vals, planes);

        _entries[key] = [start, _pack.Position - start];
        _planeEntries[key] = planes;
        if (dense != _plan.Dense) _tileLayouts[key] = dense ? "dense" : "sparse";
    }

    // Sparse CSR: @idx (offsets[markCount+1] + cellIds[nnz]) then one block per partial (values[nnz]).
    private void WriteSparse(int markCount, List<int> mkis, List<int> cellIds, List<float>[] vals,
        Dictionary<string, long[]> planes)
    {
        int nnz = cellIds.Count;
        var offsets = new int[markCount + 1];
        foreach (int mk in mkis) offsets[mk + 1]++;
        for (int i = 1; i <= markCount; i++) offsets[i] += offsets[i - 1];

        var idxType = CellIdType(_plan.Cells);
        planes[IdxPlane] = AppendBlock(
            new Schema([ListField("offsets", Int32Type.Default), ListField("cellIds", idxType)], null),
            [SingleRowList(I32(offsets), Int32Type.Default), SingleRowList(CellIdArray(cellIds, _plan.Cells), idxType)]);

        for (int i = 0; i < _plan.Partials.Count; i++)
        {
            var p = _plan.Partials[i];
            planes[p.Name] = p.Kind == PartialKind.Count
                ? AppendBlock(One(p.Name, Int32Type.Default), [SingleRowList(I32(ToInt(vals[i], nnz)), Int32Type.Default)])
                : AppendBlock(One(p.Name, FloatType.Default), [SingleRowList(F32(vals[i], nnz), FloatType.Default)]);
        }
    }

    // Dense: one cell-major plane per partial (cells × markCount); subtractable partials cumulated along the
    // ordered axis. Empty cells are 0 (additive) or NaN (min/max). Each plane is written as one independently
    // compressed block **per cell row** (all marks at one cell — the R5 slice unit, SLAB-FORMAT §4b), so the
    // client fetches only the rows a context reads. The blocks are raw little-endian typed-array bytes, not
    // Arrow: per-row Arrow framing would swamp a small tile's payload, and the row's type is known from the
    // partial. `planes[p]` is the whole-plane region (all its cell-row blocks); `slices[p]` their lengths.
    private void WriteDense(int markCount, List<int> mkis, List<int> cellIds, List<float>[] vals,
        Dictionary<string, long[]> planes, Dictionary<string, int[]> slices)
    {
        int cells = _plan.Cells;
        int size = cells * markCount;
        for (int i = 0; i < _plan.Partials.Count; i++)
        {
            var p = _plan.Partials[i];
            bool minmax = p.Kind is PartialKind.Min or PartialKind.Max;
            bool isInt = p.Kind == PartialKind.Count;
            var plane = new float[size];
            if (minmax) System.Array.Fill(plane, float.NaN);
            var col = vals[i];
            for (int e = 0; e < cellIds.Count; e++) plane[cellIds[e] * markCount + mkis[e]] = col[e];
            if (!minmax) Cumulate(plane, markCount);

            long planeStart = _pack.Position;
            var lens = new int[cells];
            var rowBytes = new byte[markCount * 4];
            for (int c = 0; c < cells; c++)
            {
                CellRowBytes(plane, c, markCount, isInt, rowBytes);
                lens[c] = AppendRaw(rowBytes, rowBytes.Length);
            }
            planes[p.Name] = [planeStart, _pack.Position - planeStart];
            slices[p.Name] = lens;
        }
    }

    // One cell row (all marks at cell c) as raw little-endian bytes: i32 (cnt, rounded) or f32.
    private static void CellRowBytes(float[] plane, int cell, int markCount, bool isInt, byte[] into)
    {
        long baseIdx = (long)cell * markCount;
        for (int m = 0; m < markCount; m++)
        {
            var span = into.AsSpan(m * 4);
            if (isInt) BinaryPrimitives.WriteInt32LittleEndian(span, checked((int)MathF.Round(plane[baseIdx + m])));
            else BinaryPrimitives.WriteSingleLittleEndian(span, plane[baseIdx + m]);
        }
    }

    // Append one raw (non-Arrow) cell-row block to the scratch file uncompressed; returns its raw length.
    // Finish transcodes it to zstd+dict. Pass a fresh copy to Sample — `raw` is a reused buffer.
    private int AppendRaw(byte[] raw, int len)
    {
        Sample(raw, len);
        _pack.Write(raw, 0, len);
        return len;
    }

    // Reservoir-sample small blocks for dictionary training (SLAB-FORMAT §5): unbiased across every level,
    // memory bounded, and biased to the small blocks the dictionary actually helps.
    private void Sample(byte[] raw, int len)
    {
        if (len > MaxSampleBlock) return;
        _sampleSeen++;
        var copy = raw[..len];
        if (_samples.Count < MaxSamples) _samples.Add(copy);
        else { int j = _rng.Next(_sampleSeen); if (j < MaxSamples) _samples[j] = copy; }
    }

    // Prefix-sum each cell along the cumulative ordered axis: cell c gains cell (c - stride) whenever its
    // coordinate on that axis is > 0. Cells visited ascending, so the predecessor is already cumulated.
    private void Cumulate(float[] plane, int markCount)
    {
        int stride = _plan.CumulativeStride, card = _plan.CumulativeCardinality, cells = _plan.Cells;
        for (int c = 0; c < cells; c++)
        {
            if (c / stride % card == 0) continue;
            int prev = (c - stride) * markCount, cur = c * markCount;
            for (int m = 0; m < markCount; m++) plane[cur + m] += plane[prev + m];
        }
    }

    // Sparse Arrow block, written raw to the scratch file (Finish transcodes to zstd+dict); returns its raw
    // [offset, length].
    private long[] AppendBlock(Schema schema, IArrowArray[] arrays)
    {
        using var batch = new RecordBatch(schema, arrays, 1);
        using var mem = new MemoryStream();
        using (var w = new ArrowStreamWriter(mem, schema, leaveOpen: true)) { w.WriteRecordBatch(batch); w.WriteEnd(); }
        byte[] bytes = mem.ToArray();
        Sample(bytes, bytes.Length);
        long offset = _pack.Position;
        _pack.Write(bytes, 0, bytes.Length);
        return [offset, bytes.Length];
    }

    public CompanionPack Finish()
    {
        _pack.Flush();
        _pack.Dispose();

        byte[]? dict = TrainDict();
        using var comp = new Compressor(ZstdLevel);
        if (dict is not null) comp.LoadDictionary(dict);

        // Pass 2 — transcode the raw scratch blocks into the final pack with zstd+dict, rebuilding the
        // directory with compressed offsets. Blocks are read a whole tile-region at a time (they are
        // contiguous in the scratch) and re-emitted tile by tile, plane by plane, so the final layout keeps
        // per-tile / per-plane grouping (whole-tile and plane-split fetches still range one region).
        var newEntries = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var newPlaneEntries = new Dictionary<string, IReadOnlyDictionary<string, long[]>>(StringComparer.Ordinal);
        var newSliceEntries = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(StringComparer.Ordinal);

        string finalPath = Path.Combine(_outputDir, FileName);
        using (var temp = File.OpenRead(_tempPath))
        using (var final = File.Create(finalPath))
        {
            foreach (var (tileKey, region) in _entries)
            {
                byte[] raw = new byte[region[1]];
                temp.Position = region[0];
                temp.ReadExactly(raw);
                long tileStart = final.Position;
                var planes = new Dictionary<string, long[]>(StringComparer.Ordinal);
                var slices = _sliceEntries.TryGetValue(tileKey, out var tileSlices) ? new Dictionary<string, int[]>(StringComparer.Ordinal) : null;

                foreach (var (plane, rawRange) in _planeEntries[tileKey])
                {
                    int inRegion = (int)(rawRange[0] - region[0]); // this plane's start within the tile region
                    long planeStart = final.Position;
                    if (slices is not null && tileSlices!.TryGetValue(plane, out var rawCellLens))
                    {
                        // Dense: one block per cell row. Transcode each; record its compressed length.
                        var compLens = new int[rawCellLens.Length];
                        int cellOff = inRegion;
                        for (int c = 0; c < rawCellLens.Length; c++)
                        {
                            compLens[c] = WriteCompressed(final, comp, raw, cellOff, rawCellLens[c]);
                            cellOff += rawCellLens[c];
                        }
                        planes[plane] = [planeStart, final.Position - planeStart];
                        slices[plane] = compLens;
                    }
                    else
                    {
                        // Sparse (or @idx): one whole block.
                        WriteCompressed(final, comp, raw, inRegion, (int)rawRange[1]);
                        planes[plane] = [planeStart, final.Position - planeStart];
                    }
                }
                newEntries[tileKey] = [tileStart, final.Position - tileStart];
                newPlaneEntries[tileKey] = planes;
                if (slices is not null) newSliceEntries[tileKey] = slices;
            }
        }
        File.Delete(_tempPath);

        string? dictPath = null, dictHash = null;
        if (dict is not null)
        {
            File.WriteAllBytes(Path.Combine(_outputDir, DictFileName), dict);
            dictPath = DictFileName;
            dictHash = Convert.ToHexStringLower(SHA256.HashData(dict));
        }

        return new CompanionPack
        {
            File = FileName,
            Codec = Codec,
            Format = "slab",
            Entries = newEntries,
            PlaneEntries = newPlaneEntries,
            SliceEntries = newSliceEntries.Count > 0 ? newSliceEntries : null,
            Dict = dictPath,
            DictHash = dictHash,
        };
    }

    // Compress raw[offset..offset+len) with the (dict-loaded) compressor and append it; returns compressed len.
    private static int WriteCompressed(FileStream final, Compressor comp, byte[] raw, int offset, int len)
    {
        byte[] packed = comp.Wrap(raw.AsSpan(offset, len)).ToArray();
        final.Write(packed, 0, packed.Length);
        return packed.Length;
    }

    // Train one dictionary per (view, version) over the sampled small blocks (SLAB-FORMAT §5). Too few
    // samples (a tiny fixture) ⇒ no dictionary; the codec is still zstd, just plain.
    private byte[]? TrainDict()
    {
        if (_samples.Count < MinSamplesForDict) return null;
        try
        {
            var dict = DictBuilder.TrainFromBuffer(_samples, DictCapacity);
            return dict.Length > 0 ? dict : null;
        }
        catch { return null; } // ZDICT can reject a sample set it deems too small/uniform — fall back to plain zstd
    }

    public void Dispose()
    {
        _pack.Dispose();
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    // ── Arrow construction (single-row List columns; the child buffer is the plane's typed array) ──────────
    private static Schema One(string name, IArrowType valueType) => new([ListField(name, valueType)], null);

    private static Field ListField(string name, IArrowType valueType) =>
        new(name, new ListType(new Field("item", valueType, false)), false);

    private static IArrowType CellIdType(int cells) =>
        cells <= 256 ? UInt8Type.Default : cells <= 65536 ? UInt16Type.Default : UInt32Type.Default;

    private static ListArray SingleRowList(IArrowArray values, IArrowType valueType)
    {
        var offsets = new ArrowBuffer.Builder<int>(2);
        offsets.Append(0);
        offsets.Append(values.Length);
        return new ListArray(new ListType(new Field("item", valueType, false)), 1,
            offsets.Build(), values, ArrowBuffer.Empty, 0, 0);
    }

    private static FloatArray F32(IReadOnlyList<float> d, int n)
    {
        var b = new ArrowBuffer.Builder<float>(n);
        for (int i = 0; i < n; i++) b.Append(d[i]);
        return new FloatArray(b.Build(), ArrowBuffer.Empty, n, 0, 0);
    }

    private static Int32Array I32(int[] d)
    {
        var b = new ArrowBuffer.Builder<int>(d.Length);
        foreach (int v in d) b.Append(v);
        return new Int32Array(b.Build(), ArrowBuffer.Empty, d.Length, 0, 0);
    }

    private static int[] ToInt(IReadOnlyList<float> d, int n)
    {
        var o = new int[n];
        for (int i = 0; i < n; i++) o[i] = checked((int)MathF.Round(d[i]));
        return o;
    }
    private static int[] ToInt(float[] d) => ToInt(d, d.Length);

    private static IArrowArray CellIdArray(List<int> cellIds, int cells)
    {
        if (cells <= 256)
        {
            var b = new ArrowBuffer.Builder<byte>(cellIds.Count);
            foreach (int c in cellIds) b.Append((byte)c);
            return new UInt8Array(b.Build(), ArrowBuffer.Empty, cellIds.Count, 0, 0);
        }
        if (cells <= 65536)
        {
            var b = new ArrowBuffer.Builder<ushort>(cellIds.Count);
            foreach (int c in cellIds) b.Append((ushort)c);
            return new UInt16Array(b.Build(), ArrowBuffer.Empty, cellIds.Count, 0, 0);
        }
        var bb = new ArrowBuffer.Builder<uint>(cellIds.Count);
        foreach (int c in cellIds) bb.Append((uint)c);
        return new UInt32Array(bb.Build(), ArrowBuffer.Empty, cellIds.Count, 0, 0);
    }
}
