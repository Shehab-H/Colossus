using System.Buffers.Binary;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Tile format 3 geometry codec (tile-transfer initiative, Phase 2). A polygon tile's geometry —
/// which in format 2 is ~69–99% of the bytes and mechanically derivable — is replaced by one self-describing
/// binary payload the client decodes back into the exact format-2 buffers (<c>polyPositions</c>,
/// <c>polyStartIndices</c>, <c>polyTriangles</c>). Lossless by construction: every branch below reconstructs
/// bit-for-bit, and <see cref="Decode"/> is the exact inverse of <see cref="Encode"/> (round-trip tests pin it).
///
/// <para>Two codecs, chosen per tile from the data (never authored):</para>
/// <list type="bullet">
/// <item><b>rect</b> — every row is a closed axis-aligned rectangle with the same vertex template; each row is
/// four u16 indices into per-tile sorted corner tables carrying the EXACT baked floats. Geometry, triangles,
/// and offsets are all derived on the client. Fires on the grid/quadkey tiles (the bulk of the corpus).</item>
/// <item><b>delta</b> — the universal fallback for real rings: de-interleave x/y, reinterpret f32 bits as u32,
/// integer delta (tiny under the extract's Hilbert order), zigzag, byte-transpose. Triangles are stored as
/// row-local indices at minimal width; their per-row boundaries derive from part_offsets (the ear-clipper emits
/// a deterministic count per part), so no triangle list offsets are stored.</item>
/// </list>
///
/// <para>Container: the payload rides in row 0 of a single Arrow binary column (<see cref="Colossus.Domain.Tiling.TileSchema.Geom3"/>);
/// the tile's measure/dict columns are untouched (single-chunk, non-null, zero-copy — format 2's contract).</para></summary>
public static class GeometryCodec
{
    public const byte Version = 1;
    public const byte CodecRect = 1;
    public const byte CodecDelta = 2;

    /// <summary>One polygon row's flattened geometry as it comes out of the extract: interleaved
    /// [x0,y0,x1,y1,…] coordinate pairs and the part offsets delimiting its rings (null = one ring).</summary>
    public readonly record struct Row(float[] Coords, int[]? PartOffsets);

    /// <summary>The format-2 geometry buffers a tile decodes to — the ground truth every codec reproduces
    /// bit-for-bit. <see cref="Positions"/> is the flat interleaved coordinate buffer (deck's polyPositions),
    /// <see cref="StartIndices"/> the per-row vertex offsets (length rows+1), <see cref="Triangles"/> the
    /// tile-global triangle indices.</summary>
    public sealed record Decoded(float[] Positions, int[] StartIndices, int[] Triangles);

    /// <summary>Builds the format-2 ground truth from a tile's rows exactly as <c>ArrowTileWriter</c> does:
    /// concatenate coords, cumulate vertex starts, triangulate each row and rebase its indices by its vertex
    /// start. This is the reference both the writer (format 2) and the codec (format 3) must agree with.</summary>
    public static Decoded BuildFormat2(IReadOnlyList<Row> rows)
    {
        int totalFloats = 0;
        foreach (var r in rows) totalFloats += r.Coords.Length;
        var positions = new float[totalFloats];
        var start = new int[rows.Count + 1];
        var tris = new List<int>();

        int floatOff = 0, vertexBase = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Array.Copy(r.Coords, 0, positions, floatOff, r.Coords.Length);
            floatOff += r.Coords.Length;
            foreach (int idx in PolygonTriangulator.Triangulate(r.Coords, r.PartOffsets)) tris.Add(idx + vertexBase);
            vertexBase += r.Coords.Length / 2;
            start[i + 1] = vertexBase;
        }
        return new Decoded(positions, start, [.. tris]);
    }

    /// <summary>Encodes a tile's rows into the format-3 payload, picking rect when every row is a congruent
    /// axis-aligned rectangle and delta otherwise. The choice is verified against <see cref="BuildFormat2"/>
    /// internally (rect only fires when it reconstructs bit-for-bit), so a mis-gate can never ship.</summary>
    public static byte[] Encode(IReadOnlyList<Row> rows) =>
        TryEncodeRect(rows, out var rect) ? rect : EncodeDelta(rows);

    // ---- rect -------------------------------------------------------------------------------------------

    private static bool TryEncodeRect(IReadOnlyList<Row> rows, out byte[] payload)
    {
        payload = [];
        if (rows.Count == 0) return false;

        int vertsPerRect = rows[0].Coords.Length / 2;
        if (vertsPerRect is < 4 or > 8) return false; // a rectangle ring is 4 corners (+optional closure)

        // The per-vertex template (which of the row's two distinct x/y each vertex uses) is read from row 0,
        // then every row must carry the identical template with each vertex exactly on a corner — which makes
        // reconstruction bit-for-bit and rejects any non-rectangular or differently-wound row.
        if (!RectRow(rows[0], out byte[] xSel, out byte[] ySel, out _, out _, out _, out _)) return false;

        // Pass 1: validate every row and collect its corner floats + the distinct values per axis.
        var rowCorners = new (float LoX, float HiX, float LoY, float HiY)[rows.Count];
        var xTable = new CornerTable();
        var yTable = new CornerTable();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Coords.Length / 2 != vertsPerRect) return false;
            if (!RectRow(rows[i], out byte[] xs, out byte[] ys, out float loX, out float hiX, out float loY, out float hiY)) return false;
            if (!xs.AsSpan().SequenceEqual(xSel) || !ys.AsSpan().SequenceEqual(ySel)) return false;
            rowCorners[i] = (loX, hiX, loY, hiY);
            xTable.Add(loX); xTable.Add(hiX); yTable.Add(loY); yTable.Add(hiY);
        }
        if (xTable.Count > ushort.MaxValue || yTable.Count > ushort.MaxValue) return false;
        xTable.Finish(); yTable.Finish();

        // Pass 2: map each row's corner floats to their table indices.
        var rowIdx = new (int X0, int X1, int Y0, int Y1)[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var (loX, hiX, loY, hiY) = rowCorners[i];
            rowIdx[i] = (xTable.IndexOf(loX), xTable.IndexOf(hiX), yTable.IndexOf(loY), yTable.IndexOf(hiY));
        }

        // The derived triangle pattern must be identical across rows (congruent rectangles) — capture row 0's.
        var pat = PolygonTriangulator.Triangulate(rows[0].Coords, rows[0].PartOffsets);
        if (pat.Count > byte.MaxValue) return false;
        foreach (int idx in pat) if (idx >= vertsPerRect) return false;
        for (int i = 1; i < rows.Count; i++)
        {
            var p = PolygonTriangulator.Triangulate(rows[i].Coords, rows[i].PartOffsets);
            if (p.Count != pat.Count) return false;
            for (int k = 0; k < p.Count; k++) if (p[k] != pat[k]) return false;
        }

        var w = new Writer();
        w.U8(CodecRect);
        w.U8(Version);
        w.U32((uint)rows.Count);
        w.U32((uint)(rows.Count * vertsPerRect));
        w.U8((byte)vertsPerRect);
        w.U8((byte)pat.Count);
        foreach (int t in pat) w.U8((byte)t);
        for (int v = 0; v < vertsPerRect; v++) w.U8(xSel[v]);
        for (int v = 0; v < vertsPerRect; v++) w.U8(ySel[v]);
        w.U16((ushort)xTable.Count);
        foreach (float f in xTable.Values) w.F32(f);
        w.U16((ushort)yTable.Count);
        foreach (float f in yTable.Values) w.F32(f);
        foreach (var (x0, x1, y0, y1) in rowIdx) { w.U16((ushort)x0); w.U16((ushort)x1); w.U16((ushort)y0); w.U16((ushort)y1); }
        payload = w.ToArray();
        return true;
    }

    // Classifies one row as an axis-aligned rectangle: exactly two distinct x and two distinct y, every vertex
    // exactly on a corner. Outputs the per-vertex selector template (0/1 into the row's lo/hi corner) and the
    // corner floats. Because each vertex is verified == one of the two exact corner floats, a matching template
    // reconstructs the row bit-for-bit — no separate reconstruction check is needed.
    private static bool RectRow(Row row, out byte[] xSel, out byte[] ySel,
        out float loX, out float hiX, out float loY, out float hiY)
    {
        xSel = ySel = [];
        loX = hiX = loY = hiY = 0;
        float[] c = row.Coords;
        int verts = c.Length / 2;
        if (verts < 4) return false;

        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        for (int v = 0; v < verts; v++)
        {
            minX = Math.Min(minX, c[2 * v]); maxX = Math.Max(maxX, c[2 * v]);
            minY = Math.Min(minY, c[2 * v + 1]); maxY = Math.Max(maxY, c[2 * v + 1]);
        }
        if (minX == maxX || minY == maxY) return false; // degenerate — not a 2×2 rectangle

        var xs = new byte[verts];
        var ys = new byte[verts];
        for (int v = 0; v < verts; v++)
        {
            float x = c[2 * v], y = c[2 * v + 1];
            if (x == minX) xs[v] = 0; else if (x == maxX) xs[v] = 1; else return false;
            if (y == minY) ys[v] = 0; else if (y == maxY) ys[v] = 1; else return false;
        }

        xSel = xs; ySel = ys;
        loX = minX; hiX = maxX; loY = minY; hiY = maxY;
        return true;
    }

    // ---- delta ------------------------------------------------------------------------------------------

    private static byte[] EncodeDelta(IReadOnlyList<Row> rows)
    {
        var f2 = BuildFormat2(rows);
        int vertexCount = f2.Positions.Length / 2;

        // De-interleave into x and y u32-bit streams.
        var xu = new uint[vertexCount];
        var yu = new uint[vertexCount];
        for (int k = 0; k < vertexCount; k++)
        {
            xu[k] = BitConverter.SingleToUInt32Bits(f2.Positions[2 * k]);
            yu[k] = BitConverter.SingleToUInt32Bits(f2.Positions[2 * k + 1]);
        }

        // Local triangle stream: subtract each row's vertex start so indices are row-local and small. The
        // per-row index boundary comes from the deterministic triangle count (the same one the client derives
        // from part_offsets), so the writer and reader slice the stream identically.
        int triMax = 0;
        var triLocal = new int[f2.Triangles.Length];
        int tOff = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            int rowTriIdx = 3 * RowTriangleCount(rows[i]);
            for (int t = 0; t < rowTriIdx; t++)
            {
                int local = f2.Triangles[tOff + t] - f2.StartIndices[i];
                triLocal[tOff + t] = local;
                triMax = Math.Max(triMax, local);
            }
            tOff += rowTriIdx;
        }
        if (tOff != f2.Triangles.Length)
            throw new InvalidOperationException($"format 3: derived triangle count {tOff} != actual {f2.Triangles.Length}");
        byte triWidth = triMax <= byte.MaxValue ? (byte)1 : (byte)2;

        var w = new Writer();
        w.U8(CodecDelta);
        w.U8(Version);
        w.U32((uint)rows.Count);
        w.U32((uint)vertexCount);
        w.U8(triWidth);

        // part_offsets per row (numParts, then the int32 values — usually [0, n]).
        foreach (var r in rows)
        {
            int[] parts = r.PartOffsets ?? [0, r.Coords.Length / 2];
            w.U16((ushort)parts.Length);
        }
        foreach (var r in rows)
        {
            int[] parts = r.PartOffsets ?? [0, r.Coords.Length / 2];
            foreach (int p in parts) w.U32((uint)p);
        }

        w.U32((uint)triLocal.Length);
        foreach (int t in triLocal) { if (triWidth == 1) w.U8((byte)t); else w.U16((ushort)t); }

        w.Bytes(ByteTransposedZigzagDelta(xu));
        w.Bytes(ByteTransposedZigzagDelta(yu));
        return w.ToArray();
    }

    // A simple ring of m unique vertices yields m−2 triangles (0 if m < 3); summed over a row's parts this is
    // the ear-clipper's deterministic per-row triangle count — the value the client re-derives from part_offsets.
    internal static int RowTriangleCount(Row row)
    {
        float[] c = row.Coords;
        int verts = c.Length / 2;
        int[] parts = row.PartOffsets is { Length: >= 2 } p ? p : [0, verts];
        int tris = 0;
        for (int q = 0; q + 1 < parts.Length; q++)
        {
            int s = parts[q], e = Math.Min(parts[q + 1], verts);
            int m = e - s;
            if (m >= 2 && c[2 * s] == c[2 * (e - 1)] && c[2 * s + 1] == c[2 * (e - 1) + 1]) m--; // closure dup
            if (m >= 3) tris += m - 2;
        }
        return tris;
    }

    private static byte[] ByteTransposedZigzagDelta(uint[] v)
    {
        int n = v.Length;
        var planes = new byte[4 * n];
        uint prev = 0;
        for (int k = 0; k < n; k++)
        {
            uint d = v[k] - prev; // wraps mod 2^32 — reversible
            prev = v[k];
            uint z = (d << 1) ^ (uint)((int)d >> 31); // zigzag
            planes[k] = (byte)z;
            planes[n + k] = (byte)(z >> 8);
            planes[2 * n + k] = (byte)(z >> 16);
            planes[3 * n + k] = (byte)(z >> 24);
        }
        return planes;
    }

    // ---- decode (exact inverse; the reference for the client + fixtures) --------------------------------

    public static Decoded Decode(byte[] payload)
    {
        var r = new Reader(payload);
        byte codec = r.U8();
        _ = r.U8(); // version
        return codec switch
        {
            CodecRect => DecodeRect(r),
            CodecDelta => DecodeDelta(r),
            _ => throw new InvalidOperationException($"format 3: unknown geometry codec {codec}"),
        };
    }

    private static Decoded DecodeRect(Reader r)
    {
        int count = (int)r.U32();
        int vertexCount = (int)r.U32();
        int vertsPerRect = r.U8();
        int triLen = r.U8();
        var pat = new int[triLen];
        for (int i = 0; i < triLen; i++) pat[i] = r.U8();
        var xSel = new byte[vertsPerRect];
        for (int v = 0; v < vertsPerRect; v++) xSel[v] = r.U8();
        var ySel = new byte[vertsPerRect];
        for (int v = 0; v < vertsPerRect; v++) ySel[v] = r.U8();
        int xTableLen = r.U16();
        var xTable = new float[xTableLen];
        for (int i = 0; i < xTableLen; i++) xTable[i] = r.F32();
        int yTableLen = r.U16();
        var yTable = new float[yTableLen];
        for (int i = 0; i < yTableLen; i++) yTable[i] = r.F32();

        var positions = new float[2 * vertexCount];
        var start = new int[count + 1];
        var tris = new int[count * triLen];
        int fo = 0, to = 0;
        for (int i = 0; i < count; i++)
        {
            float loX = xTable[r.U16()], hiX = xTable[r.U16()], loY = yTable[r.U16()], hiY = yTable[r.U16()];
            int vb = i * vertsPerRect;
            for (int v = 0; v < vertsPerRect; v++)
            {
                positions[fo++] = xSel[v] == 0 ? loX : hiX;
                positions[fo++] = ySel[v] == 0 ? loY : hiY;
            }
            for (int t = 0; t < triLen; t++) tris[to++] = pat[t] + vb;
            start[i + 1] = vb + vertsPerRect;
        }
        return new Decoded(positions, start, tris);
    }

    private static Decoded DecodeDelta(Reader r)
    {
        int count = (int)r.U32();
        int vertexCount = (int)r.U32();
        byte triWidth = r.U8();

        var numParts = new int[count];
        for (int i = 0; i < count; i++) numParts[i] = r.U16();
        var parts = new int[count][];
        for (int i = 0; i < count; i++)
        {
            parts[i] = new int[numParts[i]];
            for (int j = 0; j < numParts[i]; j++) parts[i][j] = (int)r.U32();
        }

        int triTotal = (int)r.U32();
        var triLocal = new int[triTotal];
        for (int t = 0; t < triTotal; t++) triLocal[t] = triWidth == 1 ? r.U8() : r.U16();

        uint[] xu = InverseByteTransposedZigzagDelta(r.Take(4 * vertexCount), vertexCount);
        uint[] yu = InverseByteTransposedZigzagDelta(r.Take(4 * vertexCount), vertexCount);

        var positions = new float[2 * vertexCount];
        for (int k = 0; k < vertexCount; k++)
        {
            positions[2 * k] = BitConverter.UInt32BitsToSingle(xu[k]);
            positions[2 * k + 1] = BitConverter.UInt32BitsToSingle(yu[k]);
        }

        var start = new int[count + 1];
        for (int i = 0; i < count; i++)
        {
            int rowVerts = numParts[i] >= 2 ? parts[i][^1] : 0;
            start[i + 1] = start[i] + rowVerts;
        }

        var tris = new List<int>(triTotal);
        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            int rowTriIdx = 3 * RowTriangleCountFromParts(parts[i], positions, start[i]);
            for (int t = 0; t < rowTriIdx; t++) tris.Add(triLocal[cursor + t] + start[i]);
            cursor += rowTriIdx;
        }
        return new Decoded(positions, start, [.. tris]);
    }

    // Client-side mirror of RowTriangleCount, working from the reconstructed positions + this row's parts and
    // vertex start (so the closure test reads the same bit-exact floats the encoder saw).
    private static int RowTriangleCountFromParts(int[] parts, float[] positions, int vertexStart)
    {
        if (parts.Length < 2) return 0;
        int tris = 0;
        for (int q = 0; q + 1 < parts.Length; q++)
        {
            int s = parts[q], e = parts[q + 1];
            int m = e - s;
            int a = 2 * (vertexStart + s), b = 2 * (vertexStart + e - 1);
            if (m >= 2 && positions[a] == positions[b] && positions[a + 1] == positions[b + 1]) m--;
            if (m >= 3) tris += m - 2;
        }
        return tris;
    }

    private static uint[] InverseByteTransposedZigzagDelta(ReadOnlySpan<byte> planes, int n)
    {
        var v = new uint[n];
        uint prev = 0;
        for (int k = 0; k < n; k++)
        {
            uint z = planes[k] | ((uint)planes[n + k] << 8) | ((uint)planes[2 * n + k] << 16) | ((uint)planes[3 * n + k] << 24);
            uint d = (z >> 1) ^ (uint)(-(int)(z & 1)); // inverse zigzag
            prev += d;
            v[k] = prev;
        }
        return v;
    }

    // ---- little-endian reader/writer + a small distinct-value table -------------------------------------

    // Distinct corner values per axis, sorted by their f32 bit pattern (deterministic, order-independent of
    // input). Two collecting passes: Add every value, Finish to fix the sort + index, then IndexOf per row.
    private sealed class CornerTable
    {
        private readonly HashSet<uint> _bits = new();
        private float[] _values = [];
        private Dictionary<uint, int> _index = new();

        public void Add(float v) => _bits.Add(BitConverter.SingleToUInt32Bits(v));
        public int Count => _bits.Count;

        public void Finish()
        {
            var sortedBits = _bits.ToArray();
            Array.Sort(sortedBits);
            _values = new float[sortedBits.Length];
            _index = new Dictionary<uint, int>(sortedBits.Length);
            for (int i = 0; i < sortedBits.Length; i++)
            {
                _values[i] = BitConverter.UInt32BitsToSingle(sortedBits[i]);
                _index[sortedBits[i]] = i;
            }
        }

        public IReadOnlyList<float> Values => _values;
        public int IndexOf(float v) => _index[BitConverter.SingleToUInt32Bits(v)];
    }

    private sealed class Writer
    {
        private readonly List<byte> _b = new(1024);
        public void U8(byte v) => _b.Add(v);
        public void U16(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); _b.AddRange(s); }
        public void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); _b.AddRange(s); }
        public void F32(float v) => U32(BitConverter.SingleToUInt32Bits(v));
        public void Bytes(ReadOnlySpan<byte> v) => _b.AddRange(v);
        public byte[] ToArray() => [.. _b];
    }

    private ref struct Reader(byte[] data)
    {
        private readonly byte[] _d = data;
        private int _p = 0;
        public byte U8() => _d[_p++];
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(_p)); _p += 2; return v; }
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(_p)); _p += 4; return v; }
        public float F32() => BitConverter.UInt32BitsToSingle(U32());
        public ReadOnlySpan<byte> Take(int n) { var s = _d.AsSpan(_p, n); _p += n; return s; }
    }
}
