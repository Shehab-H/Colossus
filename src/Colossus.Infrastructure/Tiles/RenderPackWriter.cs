using System.Security.Cryptography;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using ZstdSharp;

namespace Colossus.Infrastructure.Tiles;

/// <summary>Packs every render tile's columns into one archive per bake (tile-transfer Phase 3), replacing
/// the per-tile <c>z/x/y.arrow</c> files (and their brotli siblings) entirely.
///
/// <para>The lossless floor of a tile is its real-valued f32 measure planes — they barely compress, so the
/// only remaining win is not <i>sending</i> them until an interaction reads them. Each column therefore
/// becomes its own independently compressed block, and a first paint ranges only the geometry and the
/// active colour channel.</para>
///
/// <para><b>Block order is the design</b> (see <see cref="RenderPack"/>): within a tile's span the blocks
/// are laid down geometry → default colour channel → filter-slot channels → everything else, so a default
/// first paint is one contiguous range and a whole-tile read is one range over the tile's full span. The
/// order comes from the caller's role-derived channel list; nothing here keys on a channel name.</para>
///
/// <para>Two passes, mirroring <see cref="SlabCompanionWriter"/>: raw blocks to a scratch file while
/// sampling them, then transcode into the final pack with zstd + a trained shared dictionary (small
/// per-column blocks compress poorly alone). Blocks are standalone single-batch Arrow IPC streams, so a
/// column decodes on its own and stays a zero-copy view over its own inflated buffer.</para></summary>
public static class RenderPackWriter
{
    public const string FileName = "render.pack";
    public const string DictFileName = "render.dict";
    public const string Codec = "zstd";

    private const int ZstdLevel = 19;             // high level — the bake is a batch job
    private const int DictCapacity = 112 * 1024;
    private const int MinSamplesForDict = 16;     // fewer than this ⇒ no dictionary (plain zstd)
    private const int MaxSamples = 4096;

    /// <summary>Packs every tile listed in <paramref name="tiles"/> and deletes the per-tile render files.
    /// <paramref name="firstPaintChannels"/> is the view's role-derived order — the default colour channel
    /// followed by the filter-slot channels — which becomes the head of every tile's block run. Returns null
    /// (leaving the per-tile files in place) when no tile could be packed.</summary>
    public static RenderPack? Pack(
        string outputDirectory,
        IEnumerable<TileMeta> tiles,
        IReadOnlyList<string> firstPaintChannels)
    {
        string scratchPath = Path.Combine(outputDirectory, FileName + ".raw");
        var raw = new List<(string Key, string Group, long Offset, int Length)>();
        var samples = new List<byte[]>();
        var packedTiles = new List<string>();
        var firstPaintSeen = new List<string> { RenderPack.GeomGroup };

        try
        {
            using (var scratch = File.Create(scratchPath))
            {
                foreach (var tile in tiles)
                {
                    string tilePath = Path.Combine(outputDirectory, new TileId(tile.Z, tile.X, tile.Y).RelativePath);
                    if (!File.Exists(tilePath)) continue;

                    foreach (var (group, bytes) in SplitTile(tilePath, firstPaintChannels, firstPaintSeen))
                    {
                        if (samples.Count < MaxSamples) samples.Add(bytes);
                        long offset = scratch.Position;
                        scratch.Write(bytes, 0, bytes.Length);
                        raw.Add(($"{tile.Z}/{tile.X}/{tile.Y}", group, offset, bytes.Length));
                    }
                    packedTiles.Add(tilePath);
                }
            }
            if (raw.Count == 0) return null;

            byte[]? dict = TrainDict(samples);
            using var comp = new Compressor(ZstdLevel);
            if (dict is not null) comp.LoadDictionary(dict);

            // Pass 2 — transcode the raw scratch blocks into the final pack, rebuilding the directory with
            // compressed offsets. The scratch is already in pack order, so re-emitting it in sequence keeps
            // every tile's run (and the first-paint head within it) contiguous.
            var entries = new Dictionary<string, IReadOnlyDictionary<string, long[]>>(StringComparer.Ordinal);
            string packPath = Path.Combine(outputDirectory, FileName);
            using (var scratch = File.OpenRead(scratchPath))
            using (var final = File.Create(packPath))
            {
                foreach (var (key, group, offset, length) in raw)
                {
                    byte[] block = new byte[length];
                    scratch.Position = offset;
                    scratch.ReadExactly(block);
                    byte[] packed = comp.Wrap(block).ToArray();

                    long at = final.Position;
                    final.Write(packed, 0, packed.Length);
                    if (!entries.TryGetValue(key, out var groups))
                        entries[key] = groups = new Dictionary<string, long[]>(StringComparer.Ordinal);
                    ((Dictionary<string, long[]>)groups)[group] = [at, packed.Length];
                }
            }

            string? dictFile = null, dictHash = null;
            if (dict is not null)
            {
                File.WriteAllBytes(Path.Combine(outputDirectory, DictFileName), dict);
                dictFile = DictFileName;
                dictHash = Convert.ToHexString(SHA256.HashData(dict)).ToLowerInvariant();
            }

            // At rest a packed bake keeps no per-tile render file — this is the "compressed-only at rest"
            // follow-up landing: no uncompressed z/x/y.arrow and no z/x/y.arrow.br sibling.
            foreach (string tilePath in packedTiles)
            {
                File.Delete(tilePath);
                string br = tilePath + BrotliTileCompressor.SiblingSuffix;
                if (File.Exists(br)) File.Delete(br);
            }

            return new RenderPack
            {
                File = FileName,
                Codec = Codec,
                Entries = entries,
                FirstPaint = firstPaintSeen,
                Dict = dictFile,
                DictHash = dictHash,
            };
        }
        finally
        {
            if (File.Exists(scratchPath)) File.Delete(scratchPath);
        }
    }

    /// <summary>Splits one tile into its ordered groups, each a standalone single-batch Arrow IPC stream.
    /// The geometry group is whatever the mark makes derivable-in-one-block — the encoded geom3 payload for
    /// area marks, the representative x/y pair for point marks — decided from the tile's own schema, never
    /// from view config. Every other column is its own group, named after the column.</summary>
    private static IEnumerable<(string Group, byte[] Bytes)> SplitTile(
        string tilePath, IReadOnlyList<string> firstPaintChannels, List<string> firstPaintSeen)
    {
        using var stream = File.OpenRead(tilePath);
        using var reader = new ArrowStreamReader(stream);
        using var batch = reader.ReadNextRecordBatch()
            ?? throw new InvalidOperationException($"render pack: tile '{tilePath}' has no record batch");

        var schema = batch.Schema;
        var names = schema.FieldsList.Select(f => f.Name).ToList();

        var geom = new List<int>();
        int geom3 = names.IndexOf(TileSchema.Geom3);
        if (geom3 >= 0) geom.Add(geom3);
        else
        {
            int xi = names.IndexOf(TileSchema.X), yi = names.IndexOf(TileSchema.Y);
            if (xi >= 0) geom.Add(xi);
            if (yi >= 0) geom.Add(yi);
        }
        if (geom.Count == 0)
            throw new InvalidOperationException($"render pack: tile '{tilePath}' has neither '{TileSchema.Geom3}' nor '{TileSchema.X}'/'{TileSchema.Y}'");

        var emitted = new HashSet<int>(geom);
        var order = new List<(string Group, List<int> Cols)> { (RenderPack.GeomGroup, geom) };

        // First-paint head, in the caller's role-derived order, then everything else in schema order.
        foreach (string channel in firstPaintChannels)
        {
            int i = names.IndexOf(channel);
            if (i < 0 || !emitted.Add(i)) continue;
            order.Add((channel, [i]));
            if (!firstPaintSeen.Contains(channel)) firstPaintSeen.Add(channel);
        }
        for (int i = 0; i < names.Count; i++)
            if (emitted.Add(i))
                order.Add((names[i], [i]));

        foreach (var (group, cols) in order)
            yield return (group, WriteBlock(schema, batch, cols));
    }

    /// <summary>One group's columns as a standalone Arrow IPC stream. The tile's schema metadata rides on
    /// every block so a block stays self-describing (the geom3 marker in particular).</summary>
    private static byte[] WriteBlock(Schema schema, RecordBatch batch, List<int> cols)
    {
        var fields = cols.Select(i => schema.FieldsList[i]).ToList();
        var arrays = cols.Select(batch.Column).ToList();
        var blockSchema = new Schema(fields, schema.Metadata);
        // Not disposed: the arrays belong to the parent batch, which disposes them.
        var blockBatch = new RecordBatch(blockSchema, arrays, batch.Length);
        using var mem = new MemoryStream();
        using (var w = new ArrowStreamWriter(mem, blockSchema, leaveOpen: true))
        {
            w.WriteRecordBatch(blockBatch);
            w.WriteEnd();
        }
        return mem.ToArray();
    }

    // Train one dictionary per (view, version) over the sampled blocks. Too few samples (a tiny fixture) ⇒
    // no dictionary; the codec is still zstd, just plain.
    private static byte[]? TrainDict(List<byte[]> samples)
    {
        if (samples.Count < MinSamplesForDict) return null;
        try
        {
            var dict = DictBuilder.TrainFromBuffer(samples, DictCapacity);
            return dict.Length > 0 ? dict : null;
        }
        catch { return null; } // ZDICT can reject a sample set it deems too small/uniform
    }

    /// <summary>One block decompressed back to its Arrow IPC bytes. The read is bounded to
    /// <paramref name="length"/> before inflating — blocks are concatenated, so decoding must stop at the
    /// block boundary rather than run into the next one.</summary>
    public static MemoryStream ReadBlock(string packPath, long offset, long length, byte[]? dict = null)
    {
        byte[] compressed = new byte[length];
        using (var pack = File.OpenRead(packPath))
        {
            pack.Position = offset;
            pack.ReadExactly(compressed);
        }
        using var dec = new Decompressor();
        if (dict is not null) dec.LoadDictionary(dict);
        var raw = new MemoryStream(dec.Unwrap(compressed).ToArray());
        raw.Position = 0;
        return raw;
    }

    /// <summary>Rows in a packed tile, read through one of its blocks — every block in a tile's span carries
    /// the same row count, so the geometry block answers it without touching the measure planes.</summary>
    public static long RowCount(string packPath, long offset, long length, byte[]? dict = null)
    {
        using var stream = ReadBlock(packPath, offset, length, dict);
        using var reader = new ArrowStreamReader(stream);
        long count = 0;
        while (reader.ReadNextRecordBatch() is { } batch)
        {
            count += batch.Length;
            batch.Dispose();
        }
        return count;
    }
}
