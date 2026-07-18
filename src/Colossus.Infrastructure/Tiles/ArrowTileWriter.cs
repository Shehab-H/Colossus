using System.Collections;
using System.Data.Common;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Colossus.Domain.Tiling;
using DuckDB.NET.Data;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Arrow IPC tile writer (managed Apache.Arrow — no native extension). A tile with a
/// <see cref="TileSchema.Geometry"/> column also gets a bake-time <see cref="TileSchema.Triangles"/>
/// column (see <see cref="PolygonTriangulator"/>) so the client hands deck.gl ready-to-draw buffers
/// and never tessellates on the main thread.</summary>
public static class ArrowTileWriter
{
    public static void Write(DuckDBConnection conn, string selectSql, string path,
        IReadOnlySet<string>? dictionaryColumns = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? canonicalOrders = null,
        int tileFormat = 2)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        using var reader = cmd.ExecuteReader();
        var buffer = new TileBuffer(reader, skip: 0, dictionaryColumns, canonicalOrders, tileFormat);
        while (reader.Read()) buffer.AppendRow(reader);
        buffer.Flush(path);
    }

    /// <summary>Streams a query ordered by its first two columns (tile x, tile y) and writes one file
    /// per tile; those two columns are not written. Returns (tx, ty, rows) per tile. When
    /// <paramref name="tileFormat"/> is 3, each polygon tile's geometry is written as the encoded
    /// <see cref="TileSchema.Geom3"/> payload (see <see cref="GeometryCodec"/>) instead of the format-2
    /// geometry/part_offsets/triangles/x/y/id columns; measure and dict columns are unchanged.</summary>
    public static List<(long Tx, long Ty, long Rows)> WritePartitioned(
        DuckDBConnection conn, string selectSql, Func<long, long, string> pathFor,
        IReadOnlySet<string>? dictionaryColumns = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? canonicalOrders = null,
        int tileFormat = 2)
    {
        var written = new List<(long, long, long)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        using var reader = cmd.ExecuteReader();

        TileBuffer? buffer = null;
        long tx = 0, ty = 0, rows = 0;
        while (reader.Read())
        {
            long rtx = reader.GetInt64(0), rty = reader.GetInt64(1);
            if (buffer is null || rtx != tx || rty != ty)
            {
                if (buffer is not null && rows > 0)
                {
                    buffer.Flush(pathFor(tx, ty));
                    written.Add((tx, ty, rows));
                }
                buffer = new TileBuffer(reader, skip: 2, dictionaryColumns, canonicalOrders, tileFormat);
                (tx, ty, rows) = (rtx, rty, 0);
            }
            buffer.AppendRow(reader);
            rows++;
        }
        if (buffer is not null && rows > 0)
        {
            buffer.Flush(pathFor(tx, ty));
            written.Add((tx, ty, rows));
        }
        return written;
    }

    public static long RowCount(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new ArrowStreamReader(stream);
        long count = 0;
        while (reader.ReadNextRecordBatch() is { } batch)
        {
            count += batch.Length;
            batch.Dispose();
        }
        return count;
    }

    // Format-3 polygon tiles drop these columns: x/y are only read for point marks, id is read nowhere in
    // the client (the group-regime fold joins by mki), and geometry/part_offsets/triangles are all rebuilt
    // from the encoded geom3 payload. Measure and dict columns are kept exactly as format 2 (zero-copy).
    private static readonly string[] Format3Dropped =
        [TileSchema.X, TileSchema.Y, TileSchema.Id, TileSchema.Geometry, TileSchema.PartOffsets];

    // Schema metadata marker: the tile self-describes as carrying an encoded geom3 payload (the codec itself
    // is in the payload header). Additive — apache-arrow ignores unknown metadata.
    private const string Format3MetaKey = "colossus.geom3";

    /// <summary>Column builders for one tile's rows, flushed to a single Arrow IPC file. Under format 2 it
    /// detects the geometry columns and tessellates each row into a triangles list. Under format 3 it drops
    /// the derivable geometry/x/y/id columns and captures each row's geometry for <see cref="GeometryCodec"/>,
    /// writing a single self-describing <see cref="TileSchema.Geom3"/> payload instead.</summary>
    private sealed class TileBuffer
    {
        private readonly int _skip;
        private readonly int _fieldCount;
        private readonly List<(int ReaderIdx, ArrowColumnBuilder Col)> _cols = [];
        private readonly ListArray.Builder? _triangles;         // format 2 only
        private readonly List<GeometryCodec.Row>? _geomRows;    // format 3 only
        private readonly int _geometryIdx = -1;
        private readonly int _partOffsetsIdx = -1;
        private readonly bool _format3;
        // Running tile-global vertex count: each row's triangle indices are rebased by this so the
        // client takes one view over the whole child buffer instead of rebasing per row (format 2).
        private int _vertexBase;

        public TileBuffer(DbDataReader reader, int skip, IReadOnlySet<string>? dictionaryColumns,
            IReadOnlyDictionary<string, IReadOnlyList<string>>? canonicalOrders, int tileFormat)
        {
            _skip = skip;
            _fieldCount = reader.FieldCount;
            for (int i = skip; i < _fieldCount; i++)
            {
                string name = reader.GetName(i);
                if (name == TileSchema.Geometry) _geometryIdx = i;
                else if (name == TileSchema.PartOffsets) _partOffsetsIdx = i;
            }
            _format3 = tileFormat >= 3 && _geometryIdx >= 0;

            for (int i = skip; i < _fieldCount; i++)
            {
                string name = reader.GetName(i);
                if (_format3 && System.Array.IndexOf(Format3Dropped, name) >= 0) continue;
                bool dict = dictionaryColumns?.Contains(name) == true;
                _cols.Add((i, ArrowColumnBuilder.For(name, reader.GetFieldType(i), reader.GetDataTypeName(i),
                    dictionaryEncode: dict,
                    canonicalOrder: dict ? canonicalOrders?.GetValueOrDefault(name) : null)));
            }

            if (_format3) _geomRows = [];
            else _triangles = _geometryIdx >= 0 ? new ListArray.Builder(Int32Type.Default) : null;
        }

        public void AppendRow(DbDataReader reader)
        {
            object? geometry = _geometryIdx >= 0 && !reader.IsDBNull(_geometryIdx) ? reader.GetValue(_geometryIdx) : null;
            object? partOffsets = _partOffsetsIdx >= 0 && !reader.IsDBNull(_partOffsetsIdx) ? reader.GetValue(_partOffsetsIdx) : null;

            foreach (var (idx, col) in _cols)
                col.Append(reader.IsDBNull(idx) ? null : reader.GetValue(idx));

            if (_format3)
            {
                if (geometry is null)
                    throw new InvalidOperationException($"format 3: polygon tile row has null '{TileSchema.Geometry}'");
                _geomRows!.Add(new GeometryCodec.Row(ToFloatArray(geometry), partOffsets is null ? null : ToIntArray(partOffsets)));
            }
            else if (_triangles is not null) AppendTriangles(geometry, partOffsets);
        }

        // Tessellates one row's ring(s) and appends the indices rebased by the tile's running vertex
        // base, then advances that base by this row's vertex count — keeping it in lockstep with the
        // geometry column's per-row vertex offsets the client reads for polyStartIndices.
        private void AppendTriangles(object? geometry, object? partOffsets)
        {
            // Format-2 no-null contract: a polygon tile row always has geometry (real or a synthetic
            // grid-cell ring). A null here means the extract/reducer let one through — fail loudly.
            if (geometry is null)
                throw new InvalidOperationException($"format 2: polygon tile row has null '{TileSchema.Geometry}'");
            _triangles!.Append();
            float[] coords = ToFloatArray(geometry);
            int[]? parts = partOffsets is null ? null : ToIntArray(partOffsets);
            var vb = (Int32Array.Builder)_triangles.ValueBuilder;
            foreach (int idx in PolygonTriangulator.Triangulate(coords, parts)) vb.Append(idx + _vertexBase);
            _vertexBase += coords.Length / 2;
        }

        public void Flush(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var arrays = _cols.Select(c => c.Col.Build()).ToList();
            var fields = _cols.Select(c => c.Col.BuildField()).ToList();
            IReadOnlyDictionary<string, string>? metadata = null;
            int rows = _format3 ? _geomRows!.Count : (arrays.Count > 0 ? arrays[0].Length : 0);

            if (_format3)
            {
                byte[] payload = GeometryCodec.Encode(_geomRows!);
                arrays.Add(BuildGeomBlob(payload, rows));
                fields.Add(new Field(TileSchema.Geom3, BinaryType.Default, nullable: false));
                metadata = new Dictionary<string, string> { [Format3MetaKey] = "1" };
            }
            else if (_triangles is not null)
            {
                arrays.Add(_triangles.Build(default));
                fields.Add(new Field(TileSchema.Triangles, new ListType(new Field("item", Int32Type.Default, false)), nullable: false));
            }

            var schema = new Schema(fields, metadata);
            using var batch = new RecordBatch(schema, arrays, rows);
            using var stream = File.Create(path);
            using var writer = new ArrowStreamWriter(stream, schema);
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }

        // The whole encoded geometry rides in row 0 of a binary column; rows 1..n-1 are empty (non-null), so
        // the column is `rows` long like every measure column and the frame stays a single record batch. The
        // constant offset array compresses to nothing and never hits disk uncompressed.
        private static BinaryArray BuildGeomBlob(byte[] payload, int rows)
        {
            var b = new BinaryArray.Builder();
            b.Append(payload);
            for (int i = 1; i < rows; i++) b.Append(ReadOnlySpan<byte>.Empty);
            return b.Build(default);
        }
    }

    private static float[] ToFloatArray(object v)
    {
        if (v is float[] fa) return fa;
        var list = new List<float>(v is ICollection c ? c.Count : 8);
        foreach (object? e in (IEnumerable)v) if (e is not null) list.Add(Convert.ToSingle(e, CultureInfo.InvariantCulture));
        return [.. list];
    }

    private static int[] ToIntArray(object v)
    {
        if (v is int[] ia) return ia;
        var list = new List<int>(v is ICollection c ? c.Count : 4);
        foreach (object? e in (IEnumerable)v) if (e is not null) list.Add(Convert.ToInt32(e, CultureInfo.InvariantCulture));
        return [.. list];
    }
}
