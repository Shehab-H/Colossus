using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
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
    private readonly long _maxWorkingSetBytes;
    private long _sampleBytes;

    /// <summary>Per-tile layout overrides discovered while writing, or null when every tile matched the view
    /// default. The reducer folds this into <see cref="CompanionSlab.TileLayouts"/>.</summary>
    public IReadOnlyDictionary<string, string>? TileLayouts => _tileLayouts.Count > 0 ? _tileLayouts : null;

    public SlabCompanionWriter(
        string outputDirectory,
        SlabPlan plan,
        bool? layoutOverride = null,
        long maxWorkingSetBytes = 0)
    {
        _plan = plan;
        _layoutOverride = layoutOverride;
        _maxWorkingSetBytes = Math.Max(0, maxWorkingSetBytes);
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
        using var spool = new TileSpool(_outputDir, nPartials);

        long tx = 0, ty = 0;
        bool open = false;
        int maxMki = -1;

        void Flush()
        {
            if (!open || spool.Count == 0) return;
            long marks = markCounts.TryGetValue((tx, ty), out var m) ? m : maxMki + 1;
            spool.PrepareRead();
            WriteTile($"{z}/{tx}/{ty}", (int)marks, spool);
            spool.Reset();
            maxMki = -1;
        }

        // Columns: tx, ty, mki, cellId, partials… — ordered (tx, ty, mki, cellId), so entries arrive
        // mark-major with ascending cellId within a mark (the CSR order).
        while (reader.Read())
        {
            long rtx = reader.GetInt64(0), rty = reader.GetInt64(1);
            if (!open || rtx != tx || rty != ty) { Flush(); (tx, ty, open) = (rtx, rty, true); }
            int mki = reader.GetInt32(2);
            if (mki > maxMki) maxMki = mki;
            spool.Append(mki, (int)reader.GetInt64(3), reader, firstPartialColumn: 4);
        }
        Flush();
    }

    private void WriteTile(string key, int markCount, TileSpool spool)
    {
        var planes = new Dictionary<string, long[]>(StringComparer.Ordinal);
        long start = _pack.Position;

        // The per-leaf-tile gate: this tile's own occupancy, not the view's, picks its layout.
        bool dense = _layoutOverride ?? _plan.TileDense(spool.Count, markCount);
        long densePlaneBytes = (long)_plan.Cells * markCount * sizeof(float);
        if (_layoutOverride is null && _maxWorkingSetBytes > 0 && densePlaneBytes > _maxWorkingSetBytes / 2)
            dense = false;
        if (dense)
        {
            var slices = new Dictionary<string, int[]>(StringComparer.Ordinal);
            WriteDense(markCount, spool, planes, slices);
            _sliceEntries[key] = slices; // dense tiles are cell-run sliceable (R5)
        }
        else WriteSparse(markCount, spool, planes);

        _entries[key] = [start, _pack.Position - start];
        _planeEntries[key] = planes;
        if (dense != _plan.Dense) _tileLayouts[key] = dense ? "dense" : "sparse";
    }

    // Sparse CSR: @idx (offsets[markCount+1] + cellIds[nnz]) then one block per partial (values[nnz]).
    private void WriteSparse(int markCount, TileSpool spool, Dictionary<string, long[]> planes)
    {
        int nnz = spool.Count;
        var offsets = new int[markCount + 1];
        var cellIds = new int[nnz];
        int entry = 0;
        spool.ReadIndex((mki, cellId) =>
        {
            offsets[mki + 1]++;
            cellIds[entry++] = cellId;
        });
        for (int i = 1; i <= markCount; i++) offsets[i] += offsets[i - 1];

        var idxType = CellIdType(_plan.Cells);
        planes[IdxPlane] = AppendBlock(
            new Schema([ListField("offsets", Int32Type.Default), ListField("cellIds", idxType)], null),
            [SingleRowList(I32(offsets), Int32Type.Default), SingleRowList(CellIdArray(cellIds, _plan.Cells), idxType)]);

        for (int i = 0; i < _plan.Partials.Count; i++)
        {
            var p = _plan.Partials[i];
            planes[p.Name] = p.Kind == PartialKind.Count
                ? AppendBlock(One(p.Name, Int32Type.Default), [SingleRowList(I32(spool.ReadIntPartial(i)), Int32Type.Default)])
                : AppendBlock(One(p.Name, FloatType.Default), [SingleRowList(F32(spool.ReadFloatPartial(i), nnz), FloatType.Default)]);
        }
    }

    // Dense: one cell-major plane per partial (cells × markCount); subtractable partials cumulated along the
    // ordered axis. Empty cells are 0 (additive) or NaN (min/max). Each plane is written as one independently
    // compressed block **per cell row** (all marks at one cell — the R5 slice unit, SLAB-FORMAT §4b), so the
    // client fetches only the rows a context reads. The blocks are raw little-endian typed-array bytes, not
    // Arrow: per-row Arrow framing would swamp a small tile's payload, and the row's type is known from the
    // partial. `planes[p]` is the whole-plane region (all its cell-row blocks); `slices[p]` their lengths.
    private void WriteDense(int markCount, TileSpool spool, Dictionary<string, long[]> planes,
        Dictionary<string, int[]> slices)
    {
        int cells = _plan.Cells;
        int size = checked(cells * markCount);
        for (int i = 0; i < _plan.Partials.Count; i++)
        {
            var p = _plan.Partials[i];
            bool minmax = p.Kind is PartialKind.Min or PartialKind.Max;
            bool isInt = p.Kind == PartialKind.Count;
            var plane = new float[size];
            if (minmax) System.Array.Fill(plane, float.NaN);
            spool.ReadPartial(i, (mki, cellId, value) => plane[cellId * markCount + mki] = value);
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
        long sampleBudget = _maxWorkingSetBytes > 0 ? _maxWorkingSetBytes / 4 : long.MaxValue;
        if (_samples.Count < MaxSamples && _sampleBytes + copy.Length <= sampleBudget)
        {
            _samples.Add(copy);
            _sampleBytes += copy.Length;
        }
        else if (_samples.Count > 0)
        {
            int j = _rng.Next(_sampleSeen);
            if (j < _samples.Count && _sampleBytes - _samples[j].Length + copy.Length <= sampleBudget)
            {
                _sampleBytes += copy.Length - _samples[j].Length;
                _samples[j] = copy;
            }
        }
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
        long offset = _pack.Position;
        int length = checked((int)mem.Length);
        if (length <= MaxSampleBlock)
        {
            byte[] bytes = mem.ToArray();
            Sample(bytes, bytes.Length);
            _pack.Write(bytes, 0, bytes.Length);
        }
        else
        {
            mem.Position = 0;
            mem.CopyTo(_pack);
        }
        return [offset, length];
    }

    public CompanionPack Finish()
    {
        _pack.Flush();
        _pack.Dispose();

        byte[]? dict = TrainDict();
        using var comp = new Compressor(ZstdLevel);
        if (dict is not null) comp.LoadDictionary(dict);

        // Pass 2 — transcode the raw scratch blocks into the final pack with zstd+dict, rebuilding the
        // directory with compressed offsets. Re-emit tile by tile, but read only one sparse plane (or one
        // dense cell row) at a time. A hot tile's complete region can be much larger than the configured
        // working set even though each independently fetched plane remains bounded.
        var newEntries = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var newPlaneEntries = new Dictionary<string, IReadOnlyDictionary<string, long[]>>(StringComparer.Ordinal);
        var newSliceEntries = new Dictionary<string, IReadOnlyDictionary<string, int[]>>(StringComparer.Ordinal);

        string finalPath = Path.Combine(_outputDir, FileName);
        using (var temp = File.OpenRead(_tempPath))
        using (var final = File.Create(finalPath))
        {
            foreach (var (tileKey, region) in _entries)
            {
                long tileStart = final.Position;
                var planes = new Dictionary<string, long[]>(StringComparer.Ordinal);
                var slices = _sliceEntries.TryGetValue(tileKey, out var tileSlices) ? new Dictionary<string, int[]>(StringComparer.Ordinal) : null;

                foreach (var (plane, rawRange) in _planeEntries[tileKey])
                {
                    long planeStart = final.Position;
                    if (slices is not null && tileSlices!.TryGetValue(plane, out var rawCellLens))
                    {
                        // Dense: one block per cell row. Transcode each; record its compressed length.
                        var compLens = new int[rawCellLens.Length];
                        byte[] raw = new byte[rawCellLens.Length > 0 ? rawCellLens.Max() : 0];
                        temp.Position = rawRange[0];
                        for (int c = 0; c < rawCellLens.Length; c++)
                        {
                            int len = rawCellLens[c];
                            temp.ReadExactly(raw.AsSpan(0, len));
                            compLens[c] = WriteCompressed(final, comp, raw, 0, len);
                        }
                        planes[plane] = [planeStart, final.Position - planeStart];
                        slices[plane] = compLens;
                    }
                    else
                    {
                        // Sparse (or @idx): one whole block.
                        byte[] raw = new byte[checked((int)rawRange[1])];
                        temp.Position = rawRange[0];
                        temp.ReadExactly(raw);
                        WriteCompressed(final, comp, raw, 0, raw.Length);
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

    private static IArrowArray CellIdArray(IReadOnlyList<int> cellIds, int cells)
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

    /// <summary>
    /// Per-tile, columnar scratch. Companion SQL is ordered by tile but one hot tile can still contain
    /// millions of grain rows. Holding mki/cellId plus every partial in managed Lists duplicates the whole
    /// tile when Arrow buffers are built. Spooling makes ingestion O(1) memory; sparse planes materialize
    /// one at a time and dense planes stream index plus one partial into their bounded array.
    /// </summary>
    private sealed class TileSpool : IDisposable
    {
        private readonly string[] _paths;
        private readonly FileStream _index;
        private readonly FileStream[] _partials;
        private readonly BinaryWriter _indexWriter;
        private readonly BinaryWriter[] _partialWriters;

        public int Count { get; private set; }

        public TileSpool(string directory, int partials)
        {
            string stem = Path.Combine(directory, $"facts.rows-{Guid.NewGuid():N}");
            _paths = Enumerable.Range(0, partials + 1).Select(i => $"{stem}.{i}.tmp").ToArray();
            _index = Open(_paths[0]);
            _partials = Enumerable.Range(0, partials).Select(i => Open(_paths[i + 1])).ToArray();
            _indexWriter = new BinaryWriter(_index, Encoding.UTF8, leaveOpen: true);
            _partialWriters = _partials
                .Select(stream => new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                .ToArray();
        }

        private static FileStream Open(string path) =>
            new(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.SequentialScan);

        public void Append(int mki, int cellId, DbDataReader reader, int firstPartialColumn)
        {
            _indexWriter.Write(mki);
            _indexWriter.Write(cellId);
            for (int i = 0; i < _partialWriters.Length; i++)
                _partialWriters[i].Write(Convert.ToSingle(reader.GetValue(firstPartialColumn + i)));
            Count++;
        }

        public void PrepareRead()
        {
            _indexWriter.Flush();
            foreach (var writer in _partialWriters) writer.Flush();
            _index.Position = 0;
            foreach (var partial in _partials) partial.Position = 0;
        }

        public void ReadIndex(Action<int, int> visit)
        {
            _index.Position = 0;
            using var reader = new BinaryReader(_index, Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < Count; i++) visit(reader.ReadInt32(), reader.ReadInt32());
        }

        public float[] ReadFloatPartial(int partial)
        {
            var values = new float[Count];
            _partials[partial].Position = 0;
            using var reader = new BinaryReader(_partials[partial], Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < values.Length; i++) values[i] = reader.ReadSingle();
            return values;
        }

        public int[] ReadIntPartial(int partial)
        {
            var values = new int[Count];
            _partials[partial].Position = 0;
            using var reader = new BinaryReader(_partials[partial], Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < values.Length; i++) values[i] = checked((int)MathF.Round(reader.ReadSingle()));
            return values;
        }

        public void ReadPartial(int partial, Action<int, int, float> visit)
        {
            _index.Position = 0;
            _partials[partial].Position = 0;
            using var index = new BinaryReader(_index, Encoding.UTF8, leaveOpen: true);
            using var values = new BinaryReader(_partials[partial], Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < Count; i++)
                visit(index.ReadInt32(), index.ReadInt32(), values.ReadSingle());
        }

        public void Reset()
        {
            _index.SetLength(0);
            _index.Position = 0;
            foreach (var partial in _partials)
            {
                partial.SetLength(0);
                partial.Position = 0;
            }
            Count = 0;
        }

        public void Dispose()
        {
            _indexWriter.Dispose();
            foreach (var writer in _partialWriters) writer.Dispose();
            _index.Dispose();
            foreach (var partial in _partials) partial.Dispose();
            foreach (string path in _paths) if (File.Exists(path)) File.Delete(path);
        }
    }
}
