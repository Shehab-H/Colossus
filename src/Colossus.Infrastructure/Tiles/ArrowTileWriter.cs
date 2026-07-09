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
    public static void Write(DuckDBConnection conn, string selectSql, string path)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        using var reader = cmd.ExecuteReader();
        var buffer = new TileBuffer(reader, skip: 0);
        while (reader.Read()) buffer.AppendRow(reader);
        buffer.Flush(path);
    }

    /// <summary>Streams a query ordered by its first two columns (tile x, tile y) and writes one file
    /// per tile; those two columns are not written. Returns (tx, ty, rows) per tile.</summary>
    public static List<(long Tx, long Ty, long Rows)> WritePartitioned(
        DuckDBConnection conn, string selectSql, Func<long, long, string> pathFor)
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
                buffer = new TileBuffer(reader, skip: 2);
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

    /// <summary>Column builders for one tile's rows, flushed to a single Arrow IPC file. Detects the
    /// canonical geometry columns by name and, when present, tessellates each row into a triangles list.</summary>
    private sealed class TileBuffer
    {
        private readonly int _skip;
        private readonly int _fieldCount;
        private readonly ArrowColumnBuilder[] _cols;
        private readonly ListArray.Builder? _triangles;
        private readonly int _geometryIdx = -1;
        private readonly int _partOffsetsIdx = -1;

        public TileBuffer(DbDataReader reader, int skip)
        {
            _skip = skip;
            _fieldCount = reader.FieldCount;
            _cols = new ArrowColumnBuilder[_fieldCount - skip];
            for (int i = skip; i < _fieldCount; i++)
            {
                _cols[i - skip] = ArrowColumnBuilder.For(reader.GetName(i), reader.GetFieldType(i), reader.GetDataTypeName(i));
                if (reader.GetName(i) == TileSchema.Geometry) _geometryIdx = i;
                else if (reader.GetName(i) == TileSchema.PartOffsets) _partOffsetsIdx = i;
            }
            _triangles = _geometryIdx >= 0 ? new ListArray.Builder(Int32Type.Default) : null;
        }

        public void AppendRow(DbDataReader reader)
        {
            object? geometry = null, partOffsets = null;
            for (int i = _skip; i < _fieldCount; i++)
            {
                object? v = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (i == _geometryIdx) geometry = v;
                else if (i == _partOffsetsIdx) partOffsets = v;
                _cols[i - _skip].Append(v);
            }
            if (_triangles is not null) AppendTriangles(_triangles, geometry, partOffsets);
        }

        public void Flush(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var arrays = _cols.Select(c => c.Build()).ToList();
            var fields = _cols.Select(c => c.Field).ToList();
            if (_triangles is not null)
            {
                arrays.Add(_triangles.Build(default));
                fields.Add(new Field(TileSchema.Triangles, new ListType(new Field("item", Int32Type.Default, true)), nullable: true));
            }
            var schema = new Schema(fields, null);
            int rows = arrays.Count > 0 ? arrays[0].Length : 0;
            using var batch = new RecordBatch(schema, arrays, rows);
            using var stream = File.Create(path);
            using var writer = new ArrowStreamWriter(stream, schema);
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }
    }

    private static void AppendTriangles(ListArray.Builder triangles, object? geometry, object? partOffsets)
    {
        triangles.Append();
        if (geometry is null) return;
        float[] coords = ToFloatArray(geometry);
        int[]? parts = partOffsets is null ? null : ToIntArray(partOffsets);
        var vb = (Int32Array.Builder)triangles.ValueBuilder;
        foreach (int idx in PolygonTriangulator.Triangulate(coords, parts)) vb.Append(idx);
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
