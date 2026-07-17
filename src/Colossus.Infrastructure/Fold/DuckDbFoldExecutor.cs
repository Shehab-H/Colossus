using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;

namespace Colossus.Infrastructure.Fold;

/// <summary>The R4 server fold (companion-scale REQUIREMENTS.md R4): given a fold context + tile keys, folds
/// the view's measures on the server over the BAKED facts Parquet (RULES R5 — never the source DB) and
/// returns per-tile, mki-keyed Arrow columns, behind the same seam the client fold serves. It reproduces the
/// companion cells the client would fetch (the f32 grain partials — <see cref="FoldMeasureSql.CellPartialSql"/>)
/// and folds them in DUCKDB exactly as the client folds them in JS, so remote and local results are
/// bit-for-bit identical. The mki join reproduces the reducer's per-tile mark ordering (<see cref="MarkKey"/>
/// over <see cref="TileSql"/> indices), so a column indexes the rendered marks with no string join.</summary>
public sealed class DuckDbFoldExecutor
{
    // Mirrors AggregateReducer's LOD constants — the zreal tagging must reproduce the bake's exactly (a
    // parity test over the real bake is the conformance witness).
    private const int ZCap = 16;
    private const double ZSlack = 0.02;

    public FoldResponse Fold(Manifest manifest, string factsParquetPath, IReadOnlyList<string> measureNames,
        FoldContextDto context, IReadOnlyList<string> tileKeys)
    {
        var measures = ResolveMeasures(manifest, measureNames);
        var grain = manifest.GrainChannels ?? [];
        var partials = MeasurePartials.For(measures.Select(m => m.Ast));
        var tilesByZ = GroupTilesByZoom(tileKeys);

        // In-memory: the fold is read-only and viewport-scoped, and a file-backed session would write its
        // database + spill into the immutable version directory (RULES R7) and race concurrent folds.
        using var db = DuckDbSession.InMemory();
        var types = ColumnTypes(db, factsParquetPath);
        var grainInfo = grain.ToDictionary(
            g => g,
            g => (Temporal: IsTemporal(manifest, g), DuckType: types.GetValueOrDefault(g, "")),
            StringComparer.Ordinal);

        // One builder set for the whole response; tiles are concatenated in query order and the per-tile row
        // directory (tileKey → count) rides in the schema metadata, so there is no per-row tile column.
        var builders = new object[measures.Count];
        for (int i = 0; i < measures.Count; i++)
            builders[i] = measures[i].IsArgmax ? new UInt16Array.Builder() : (object)new FloatArray.Builder();
        var directory = new List<(string Tile, int Count)>();

        var sw = Stopwatch.StartNew();
        foreach (var (z, tiles) in tilesByZ)
        {
            string sql = BuildLevelSql(manifest, factsParquetPath, z, tiles, grain, grainInfo, partials, measures, context);
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();

            // Column 0 is the tile key (boundary marker, not emitted); columns 1.. are the measures in order.
            string? curTile = null;
            int runCount = 0;
            while (reader.Read())
            {
                string tile = reader.GetString(0);
                if (tile != curTile)
                {
                    if (curTile is not null) directory.Add((curTile, runCount));
                    curTile = tile;
                    runCount = 0;
                }
                runCount++;
                for (int i = 0; i < measures.Count; i++)
                {
                    if (measures[i].IsArgmax)
                        ((UInt16Array.Builder)builders[i]).Append(reader.IsDBNull(i + 1) ? (ushort)0xFFFF : Convert.ToUInt16(reader.GetValue(i + 1), CultureInfo.InvariantCulture));
                    else
                        ((FloatArray.Builder)builders[i]).Append(reader.IsDBNull(i + 1) ? float.NaN : Convert.ToSingle(reader.GetValue(i + 1), CultureInfo.InvariantCulture));
                }
            }
            if (curTile is not null) directory.Add((curTile, runCount));
        }
        long foldMs = sw.ElapsedMilliseconds;

        byte[] bytes = Serialize(measures, builders, directory);
        return new FoldResponse(bytes, foldMs, directory.Sum(d => d.Count));
    }

    private static List<ResolvedMeasure> ResolveMeasures(Manifest manifest, IReadOnlyList<string> names)
    {
        var byName = (manifest.View.Measures ?? []).ToDictionary(m => m.Name, m => m.Expr, StringComparer.Ordinal);
        var result = new List<ResolvedMeasure>();
        foreach (var name in names)
        {
            if (!byName.TryGetValue(name, out var expr))
                throw new ArgumentException($"unknown measure '{name}'");
            var ast = MeasureParser.Parse(expr);
            result.Add(new ResolvedMeasure(name, ast, ast is ArgExt));
        }
        return result;
    }

    private static List<(int Z, List<(int X, int Y)> Tiles)> GroupTilesByZoom(IReadOnlyList<string> keys)
    {
        var byZ = new Dictionary<int, List<(int, int)>>();
        foreach (var key in keys)
        {
            var parts = key.Split('/');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out int z)
                || !int.TryParse(parts[1], out int x)
                || !int.TryParse(parts[2], out int y))
                throw new ArgumentException($"invalid tile key '{key}'");
            (byZ.TryGetValue(z, out var list) ? list : byZ[z] = []).Add((x, y));
        }
        return byZ.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private string BuildLevelSql(Manifest manifest, string factsPath, int z, List<(int X, int Y)> tiles,
        IReadOnlyList<string> grain, Dictionary<string, (bool Temporal, string DuckType)> grainInfo,
        IReadOnlyList<Partial> partials, List<ResolvedMeasure> measures, FoldContextDto context)
    {
        var root = manifest.Root;
        string txExpr = TileSql.TileIndex(root, z, TileSql.Axis.X);
        string tyExpr = TileSql.TileIndex(root, z, TileSql.Axis.Y);
        string gxExpr = TileSql.GridIndex(root, z, TileSql.Axis.X);
        string gyExpr = TileSql.GridIndex(root, z, TileSql.Axis.Y);

        // Prefilter to the viewport's union bbox (a cheap x/y range) so zreal/tiling is computed only over
        // the on-screen facts, not the whole extract; the exact (tx,ty) join below is the membership authority.
        var (minX, minY, maxX, maxY) = ViewportBox(root, z, tiles);
        string prefilter = $"\"{TileSchema.X}\" >= {Sql.Dbl(minX)} AND \"{TileSchema.X}\" <= {Sql.Dbl(maxX)} AND " +
                           $"\"{TileSchema.Y}\" >= {Sql.Dbl(minY)} AND \"{TileSchema.Y}\" <= {Sql.Dbl(maxY)}";

        string req = string.Join(", ", tiles.Select(t => $"({t.X}, {t.Y})"));
        string grainCols = string.Concat(grain.Select(g => $", \"{g}\""));
        string cellPartials = string.Concat(partials.Select(p => $", {FoldMeasureSql.CellPartialSql(p)}"));
        string ctxPred = FoldSql.ContextPredicate(context, grainInfo);

        // The tiling half: re-derive each fact's zreal / tile / grid cell exactly as the reducer did, key it
        // to its mark (real, or the ~1px cell it merged into), assign mki in the render tile's own order,
        // and re-derive the grain cells — the companion the client would have fetched. The fold tail then
        // runs the frozen measure semantics over those cells.
        string tileExpr = $"({z}::VARCHAR || '/' || marks.tx::VARCHAR || '/' || marks.ty::VARCHAR)";
        return $"""
            WITH req(rtx, rty) AS (VALUES {req}),
            scoped AS (
                SELECT * FROM read_parquet('{Sql.Path(factsPath)}') WHERE {prefilter}
            ),
            tagged AS (
                SELECT *, {ExtentExpr} AS ext, {txExpr} AS tx, {tyExpr} AS ty, {gxExpr} AS gx, {gyExpr} AS gy
                FROM scoped
            ),
            f AS (
                SELECT *, CASE WHEN ext IS NULL OR ext <= 0 THEN {ZCap}
                    ELSE CAST(greatest(0, least({ZCap}, ceil(log2({Sql.Lit(root.SpanX)} / ({TileSchema.GridPerTile} * ext)) - {Sql.Lit(ZSlack)}))) AS INTEGER)
                    END AS zreal
                FROM tagged
            ),
            tile AS (
                SELECT f.*, CASE WHEN zreal <= {z} THEN {MarkKey.RealSql()} ELSE {MarkKey.MergedSql("gx", "gy")} END AS mk
                FROM f JOIN req ON f.tx = req.rtx AND f.ty = req.rty
            ),
            marks AS (
                SELECT tx, ty, mk, (row_number() OVER (PARTITION BY tx, ty ORDER BY mk) - 1)::INTEGER AS mki
                FROM (SELECT DISTINCT tx, ty, mk FROM tile)
            ),
            cells AS (
                SELECT tx, ty, mk{grainCols}{cellPartials}
                FROM tile GROUP BY tx, ty, mk{grainCols}
            )
            {FoldSql.Tail(measures, ctxPred, arg => ArgmaxDomain(manifest, arg.Dimension), tileExpr)}
            """;
    }

    // The exact ext expression AggregateReducer.LoadTagged computes (span of the wider geometry axis).
    private static string ExtentExpr =>
        $"greatest(list_max(list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = 1)) - list_min(list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = 1)), " +
        $"list_max(list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = 0)) - list_min(list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = 0)))";

    private static (double MinX, double MinY, double MaxX, double MaxY) ViewportBox(Bbox root, int z, List<(int X, int Y)> tiles)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in tiles)
        {
            var (xMin, yMin, xMax, yMax) = TileMath.TileRect(root, new TileId(z, x, y));
            minX = Math.Min(minX, xMin); minY = Math.Min(minY, yMin);
            maxX = Math.Max(maxX, xMax); maxY = Math.Max(maxY, yMax);
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>Whether a grain channel is temporal (declared date/temporal role) — drives the canonical
    /// ISO rendering. Data-agnostic: it follows the declared role, never the channel name.</summary>
    private static bool IsTemporal(Manifest manifest, string channel)
    {
        var c = manifest.View.Source.Channels.FirstOrDefault(x => x.Name == channel);
        return c is not null && (c.Type == ChannelType.Date || c.Role == ChannelRole.Temporal);
    }

    private static Dictionary<string, string> ColumnTypes(DuckDbSession db, string factsPath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{Sql.Path(factsPath)}')";
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    // The argmax colour domain = the dimension's canonical domain (manifest channelDomains, as the client
    // uses), falling back to the slab axis domain.
    private static IReadOnlyList<string> ArgmaxDomain(Manifest manifest, string dimension)
    {
        if (manifest.ChannelDomains?.GetValueOrDefault(dimension)?.Values is { Count: > 0 } v) return v;
        var axis = manifest.CompanionSlab?.Axes.FirstOrDefault(a => a.Name == dimension);
        return axis?.Domain ?? [];
    }

    private static byte[] Serialize(List<ResolvedMeasure> measures, object[] builders,
        List<(string Tile, int Count)> directory)
    {
        var arrays = new IArrowArray[measures.Count];
        var fields = new Field[measures.Count];
        for (int i = 0; i < measures.Count; i++)
        {
            arrays[i] = measures[i].IsArgmax ? ((UInt16Array.Builder)builders[i]).Build() : ((FloatArray.Builder)builders[i]).Build();
            var type = measures[i].IsArgmax ? (IArrowType)UInt16Type.Default : FloatType.Default;
            fields[i] = new Field(measures[i].Name, type, nullable: false);
        }

        // The per-tile row directory rides in schema metadata so there is no per-row tile column: the client
        // slices each measure column by these contiguous [tileKey → mki count] runs (rows are mki-ordered).
        var dirJson = JsonSerializer.Serialize(directory.Select(d => new object[] { d.Tile, d.Count }));
        var metadata = new Dictionary<string, string> { ["tiles"] = dirJson };
        var schema = new Schema(fields, metadata);
        int rows = directory.Sum(d => d.Count);

        using var batch = new RecordBatch(schema, arrays, rows);
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema))
        {
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }
        return stream.ToArray();
    }
}

/// <summary>The folded Arrow IPC plus the server-side fold time (surfaced as a response header for the
/// benchmark) and total mark rows.</summary>
public sealed record FoldResponse(byte[] Arrow, long FoldMs, int Rows);
