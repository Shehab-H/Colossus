using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;

namespace Colossus.Infrastructure.Tiles;

/// <summary>One decoded slab tile: sparse carries <see cref="Offsets"/>+<see cref="CellIds"/> plus the
/// partial planes as typed arrays; dense carries only the (cell-major, cumulative) planes. The child buffer
/// of each single-row Arrow List block is the plane's array (SLAB-FORMAT §5).</summary>
public sealed record SlabTile(
    bool Dense,
    int[]? Offsets,
    int[]? CellIds,
    IReadOnlyDictionary<string, float[]> FloatPlanes,
    IReadOnlyDictionary<string, int[]> IntPlanes);

/// <summary>Reads slab companion blocks back out of the pack — for the verifier's fact witness and the
/// cross-language fixture test. Each plane is one independently gzip-compressed Arrow block, decoded via the
/// same bounded read the R2 pack uses.</summary>
public static class SlabCompanionReader
{
    public static SlabTile Read(string packPath, IReadOnlyDictionary<string, long[]> planes, CompanionSlab slab)
    {
        bool dense = slab.Layout == "dense";
        int[]? offsets = null, cellIds = null;
        if (!dense && planes.TryGetValue(SlabCompanionWriter.IdxPlane, out var idx))
        {
            using var b = ReadBatch(packPath, idx);
            offsets = IntChild(b, "offsets");
            cellIds = IntChild(b, "cellIds");
        }

        var fp = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var ip = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var p in slab.Partials)
        {
            if (!planes.TryGetValue(p.Name, out var r)) continue;
            using var b = ReadBatch(packPath, r);
            if (p.Type == "i32") ip[p.Name] = IntChild(b, p.Name);
            else fp[p.Name] = FloatChild(b, p.Name);
        }
        return new SlabTile(dense, offsets, cellIds, fp, ip);
    }

    /// <summary>Source facts this tile stands for — the verifier's witness (SLAB-FORMAT §7). Σ cnt when a
    /// cnt plane exists (dense: the cumulative last-bin per cat run; sparse: every entry); else sparse nnz
    /// (equals facts when grain is unique — the reference data — matching the row form's row-count witness).
    /// −1 when neither is available (a dense view with no count/avg measure — no registered view).</summary>
    public static long Facts(SlabTile t, CompanionSlab slab)
    {
        if (t.IntPlanes.TryGetValue("cnt", out var cnt))
            return t.Dense ? DenseRawTotal(cnt, slab) : Sum(cnt);
        return !t.Dense && t.CellIds is not null ? t.CellIds.Length : -1;
    }

    // Dense cnt is cumulative along the ordered axis, so the run total is its last bin. Sum those over marks.
    private static long DenseRawTotal(int[] cnt, CompanionSlab slab)
    {
        int cells = slab.Cells, markCount = cells > 0 ? cnt.Length / cells : 0;
        int idx = -1;
        for (int i = 0; i < slab.Axes.Count; i++) if (slab.Axes[i].Cumulative) idx = i;
        if (idx < 0) return Sum(cnt); // no cumulative axis: planes are raw
        int stride = 1;
        for (int j = idx + 1; j < slab.Axes.Count; j++) stride *= slab.Axes[j].Cardinality;
        int card = slab.Axes[idx].Cardinality;
        long total = 0;
        for (int c = 0; c < cells; c++)
            if (c / stride % card == card - 1)
                for (int m = 0; m < markCount; m++) total += cnt[c * markCount + m];
        return total;
    }

    private static long Sum(int[] a) { long s = 0; foreach (int v in a) s += v; return s; }

    private static RecordBatch ReadBatch(string packPath, long[] range)
    {
        using var stream = CompanionPackWriter.ReadBlock(packPath, range[0], range[1]);
        using var reader = new ArrowStreamReader(stream);
        return reader.ReadNextRecordBatch()!;
    }

    private static IArrowArray Child(RecordBatch b, string col) => ((ListArray)b.Column(col)).Values;

    private static int[] IntChild(RecordBatch b, string col) => Child(b, col) switch
    {
        Int32Array a => a.Values.ToArray(),
        UInt8Array a => Widen(a.Values),
        UInt16Array a => Widen(a.Values),
        UInt32Array a => Widen(a.Values),
        var other => throw new NotSupportedException($"slab int child '{col}' is {other.GetType().Name}"),
    };

    private static float[] FloatChild(RecordBatch b, string col) => ((FloatArray)Child(b, col)).Values.ToArray();

    private static int[] Widen(ReadOnlySpan<byte> s) { var o = new int[s.Length]; for (int i = 0; i < s.Length; i++) o[i] = s[i]; return o; }
    private static int[] Widen(ReadOnlySpan<ushort> s) { var o = new int[s.Length]; for (int i = 0; i < s.Length; i++) o[i] = s[i]; return o; }
    private static int[] Widen(ReadOnlySpan<uint> s) { var o = new int[s.Length]; for (int i = 0; i < s.Length; i++) o[i] = (int)s[i]; return o; }
}
