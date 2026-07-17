using System.IO.Compression;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Writes fact companions as slabs (docs/companion-scale/SLAB-FORMAT.md) into one per-bake archive
/// (<c>facts.pack</c>). Streams the companion grain rows of each level — <c>(tx, ty, mki, cellId, partials…)</c>
/// ordered by <c>(tx, ty, mki, cellId)</c> — and, per tile, encodes either sparse CSR or dense cumulative
/// cell-major planes, writing each plane (and, for sparse, the <c>@idx</c> structure) as an independently
/// gzip-compressed Arrow block. The directory (<see cref="CompanionPack.Entries"/> whole-tile regions,
/// <see cref="CompanionPack.PlaneEntries"/> per-plane ranges) rides the manifest. Both leaf and internal
/// levels are packed — internal companions are compressed and range-fetchable too (R5).</summary>
internal sealed class SlabCompanionWriter : IDisposable
{
    public const string FileName = CompanionPackWriter.FileName; // "facts.pack"
    public const string Codec = CompanionPackWriter.Codec;       // "gzip"
    public const string IdxPlane = "@idx";

    private readonly SlabPlan _plan;
    // Test support only: forces every tile's layout instead of the per-tile occupancy gate, so the shared
    // fixture can pin the dense encoder against a tile whose occupancy would otherwise select sparse. Null in
    // production (the gate decides per tile).
    private readonly bool? _layoutOverride;
    private readonly FileStream _pack;
    private readonly Dictionary<string, long[]> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, long[]>> _planeEntries = new(StringComparer.Ordinal);
    // Tiles whose per-tile layout (SLAB-FORMAT §3) differs from the view default (_plan.Dense) — the only
    // ones the manifest records (CompanionSlab.TileLayouts); the rest fall back to the default.
    private readonly Dictionary<string, string> _tileLayouts = new(StringComparer.Ordinal);

    /// <summary>Per-tile layout overrides discovered while writing, or null when every tile matched the view
    /// default. The reducer folds this into <see cref="CompanionSlab.TileLayouts"/>.</summary>
    public IReadOnlyDictionary<string, string>? TileLayouts => _tileLayouts.Count > 0 ? _tileLayouts : null;

    public SlabCompanionWriter(string outputDirectory, SlabPlan plan, bool? layoutOverride = null)
    {
        _plan = plan;
        _layoutOverride = layoutOverride;
        Directory.CreateDirectory(outputDirectory);
        _pack = File.Create(Path.Combine(outputDirectory, FileName));
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
        if (dense) WriteDense(markCount, mkis, cellIds, vals, planes);
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
    // ordered axis. Empty cells are 0 (additive) or NaN (min/max).
    private void WriteDense(int markCount, List<int> mkis, List<int> cellIds, List<float>[] vals,
        Dictionary<string, long[]> planes)
    {
        int size = _plan.Cells * markCount;
        for (int i = 0; i < _plan.Partials.Count; i++)
        {
            var p = _plan.Partials[i];
            bool minmax = p.Kind is PartialKind.Min or PartialKind.Max;
            var plane = new float[size];
            if (minmax) System.Array.Fill(plane, float.NaN);
            var col = vals[i];
            for (int e = 0; e < cellIds.Count; e++) plane[cellIds[e] * markCount + mkis[e]] = col[e];
            if (!minmax) Cumulate(plane, markCount);
            planes[p.Name] = p.Kind == PartialKind.Count
                ? AppendBlock(One(p.Name, Int32Type.Default), [SingleRowList(I32(ToInt(plane)), Int32Type.Default)])
                : AppendBlock(One(p.Name, FloatType.Default), [SingleRowList(F32(plane, plane.Length), FloatType.Default)]);
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

    private long[] AppendBlock(Schema schema, IArrowArray[] arrays)
    {
        using var batch = new RecordBatch(schema, arrays, 1);
        using var mem = new MemoryStream();
        using (var w = new ArrowStreamWriter(mem, schema, leaveOpen: true)) { w.WriteRecordBatch(batch); w.WriteEnd(); }
        mem.Position = 0;
        long offset = _pack.Position;
        using (var gz = new GZipStream(_pack, CompressionLevel.Optimal, leaveOpen: true)) mem.CopyTo(gz);
        return [offset, _pack.Position - offset];
    }

    public CompanionPack Finish()
    {
        _pack.Flush();
        _pack.Dispose();
        return new CompanionPack
        {
            File = FileName,
            Codec = Codec,
            Format = "slab",
            Entries = _entries,
            PlaneEntries = _planeEntries,
        };
    }

    public void Dispose() => _pack.Dispose();

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
