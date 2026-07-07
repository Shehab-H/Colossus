using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;

namespace Colossus.Core;

/// <summary>
/// Reads/writes tile files in the Arrow IPC file format. The on-disk layout is the exact typed-array
/// layout the browser feeds to deck.gl binary attributes — no parsing, no per-mark objects. Positions
/// are absolute float32 for M1 (tile-relative quantization is a later bandwidth optimization).
/// </summary>
public static class ArrowIo
{
    public const string XField = "x";
    public const string YField = "y";
    public const string ValueField = "value";
    public const string CategoryField = "category";

    public static readonly Schema TileSchema = new(
        new List<Field>
        {
            new(XField, FloatType.Default, nullable: false),
            new(YField, FloatType.Default, nullable: false),
            new(ValueField, FloatType.Default, nullable: false),
            new(CategoryField, UInt8Type.Default, nullable: false),
        },
        metadata: null);

    /// <summary>Writes one tile. Arrays must each be exactly <paramref name="count"/> long.</summary>
    public static void WriteTile(string path, float[] x, float[] y, float[] value, byte[] category, int count)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var xb = new FloatArray.Builder(); xb.Reserve(count);
        var yb = new FloatArray.Builder(); yb.Reserve(count);
        var vb = new FloatArray.Builder(); vb.Reserve(count);
        var cb = new UInt8Array.Builder(); cb.Reserve(count);
        for (int i = 0; i < count; i++)
        {
            xb.Append(x[i]);
            yb.Append(y[i]);
            vb.Append(value[i]);
            cb.Append(category[i]);
        }

        var batch = new RecordBatch(TileSchema,
            new IArrowArray[] { xb.Build(), yb.Build(), vb.Build(), cb.Build() }, count);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new ArrowFileWriter(stream, TileSchema);
        writer.WriteRecordBatch(batch);
        writer.WriteEnd();
    }

    /// <summary>Total row count across all record batches in a tile file (used by the fidelity test).</summary>
    public static long ReadRowCount(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new ArrowFileReader(stream);
        long total = 0;
        RecordBatch? batch;
        while ((batch = reader.ReadNextRecordBatch()) is not null)
            total += batch.Length;
        return total;
    }
}
