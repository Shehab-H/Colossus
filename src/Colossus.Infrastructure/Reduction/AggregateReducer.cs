using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Tiling;
using Axis = Colossus.Infrastructure.Tiling.TileSql.Axis;
using Colossus.Infrastructure.Configuration;

namespace Colossus.Infrastructure.Reduction;

/// <summary>Pixel pyramid: a polygon renders as itself while it spans ≥1 screen pixel; anything
/// smaller merges into its single ~1px grid cell (mean per measure). Zoomed out is therefore the
/// zoomed-in picture at screen resolution — LOD is invisible. A tile is a leaf once everything in
/// it is real, so effective resolution is uniform across the viewport at any zoom. All tile/grid math
/// is rendered through <see cref="TileSql"/>; column names come from <see cref="TileSchema"/>.</summary>
public sealed class AggregateReducer(BakeResourceOptions? resources = null) : IReductionStrategy
{
    public ReductionKind Kind => ReductionKind.Aggregate;

    private const int ZCap = 16;
    // Slack on the ≥1px test so float32 coordinate noise can't split equal-sized polygons across levels.
    private const double ZSlack = 0.02;
    // The R4 server fold's input, beside the tiles in the version directory (manifest.factsParquet).
    private const string FoldFactsFile = "facts.parquet";

    public ReductionResult Reduce(ReductionContext ctx)
    {
        var tiles = new List<TileMeta>();
        long leafTotal = 0;
        using var db = DuckDbSession.OnDisk(Path.GetDirectoryName(Path.GetFullPath(ctx.StagingParquetPath))!, resources);
        var stagingColumns = ParquetColumns(db, ctx.StagingParquetPath);
        bool hasShape = stagingColumns.Contains(TileSchema.Geometry) && stagingColumns.Contains(TileSchema.PartOffsets);
        LoadTagged(db, "t", ctx.StagingParquetPath, ctx.Root, hasShape, includeGeometryIdentity: false, ctx.MaxZoom);

        // Group regime: facts share each mark's geometry, so a fact's zreal equals its mark's. The slab
        // plan (layout from measured occupancy + axes) is decided once here, then every level's companion
        // is streamed into one facts.pack (companion-scale R1); leaf and internal levels alike.
        SlabPlan? plan = null;
        using SlabCompanionWriter? slab = ctx.Companion is { } companion
            ? BuildSlabWriter(db, ctx, companion, resources, out plan)
            : null;

        BuildPyramid(db, ctx, tiles, ref leafTotal, slab, plan, hasShape);

        var pack = slab?.Finish();
        // Fold the per-tile layout overrides the writer discovered into the slab metadata (SLAB-FORMAT §3).
        var slabMeta = plan?.ToManifest();
        if (slabMeta is not null && slab?.TileLayouts is { } tileLayouts)
            slabMeta = slabMeta with { TileLayouts = tileLayouts };
        return new ReductionResult(tiles, leafTotal, pack, slabMeta,
            ctx.Companion is not null ? FoldFactsFile : null);
    }

    // Facts share each mark's geometry, so a fact's zreal equals its mark's — the merge decision the marks
    // pyramid makes and the companion mirrors are the same. The slab plan (layout from measured occupancy +
    // axes) is decided once here from the loaded facts/marks.
    private static SlabCompanionWriter BuildSlabWriter(
        DuckDbSession db, ReductionContext ctx, CompanionSpec companion,
        BakeResourceOptions? resources, out SlabPlan plan)
    {
        Directory.CreateDirectory(ctx.OutputDirectory);
        var facts = LoadCompanionFacts(
            db, companion, Path.Combine(ctx.OutputDirectory, FoldFactsFile), resources);
        Console.WriteLine($"AggregateReducer companion facts ready: {facts.Rows:N0} rows, {facts.BatchCount} budget ranges, {facts.LeafNnz:N0} leaf cells.");
        plan = SlabPlanner.Plan(db, companion, "facts", "t", facts.LeafNnz, facts.AxisDomains);
        Console.WriteLine($"AggregateReducer slab plan ready: {plan.Cells} cells, occupancy {plan.Occupancy:0.000000}.");
        return new SlabCompanionWriter(ctx.OutputDirectory, plan,
            maxWorkingSetBytes: resources?.TileWorkingSetBytes ?? 0);
    }

    // Facts share geometry with the grouped marks, whose zreal was already derived while loading `t`.
    // Materializing SELECT * here used to duplicate every polygon coordinate list for every fact (24M facts
    // over ~2M marks in mobile-coverage) and recompute the same extent repeatedly. That work sits before the
    // slab writer's bounded tile buffers, so it could exceed the bake envelope despite every later buffer
    // respecting it. Keep only columns the companion SQL reads and join the mark's already-computed zreal by
    // the persisted geometry fingerprint. A single 24M-row join+sort can make DuckDB's external-sort files,
    // page cache and native buffers overlap even when its buffer pool is capped. Split the x-ordered stream
    // into budget-derived ranges, sort each range, then concatenate those already ordered streams through the
    // same Parquet writer. The final schema, global x/y order, row-group boundaries and bytes therefore do not
    // depend on the batch count. The marks' public id is `p:<hex fingerprint>`; decode it on the bounded side
    // of each join so both the fact sort and mark hash are proportional to the configured envelope.
    internal static CompanionFactsWriteResult LoadCompanionFacts(
        DuckDbSession db,
        CompanionSpec companion,
        string foldFactsPath,
        BakeResourceOptions? resources)
    {
        var sourceColumns = ParquetColumnList(db, companion.FactsParquetPath);
        var present = sourceColumns.ToHashSet(StringComparer.Ordinal);
        var required = new List<string>
        {
            TileSchema.X,
            TileSchema.Y,
            MarkKey.GeometryIdentity,
        };
        var requiredSet = required.ToHashSet(StringComparer.Ordinal);
        void Require(string column)
        {
            if (requiredSet.Add(column)) required.Add(column);
        }

        foreach (var channel in companion.GrainChannels) Require(channel.Name);
        foreach (var partial in companion.Partials)
        {
            if (partial.Channel is { Length: > 0 } channel) Require(channel);
            if (partial.Weight is { Length: > 0 } weight) Require(weight);
        }

        string[] missing = required.Where(column => !present.Contains(column)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException(
                $"Companion facts are missing required columns: {string.Join(", ", missing)}.");

        static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
        // file_row_number is an internal tie-breaker only. It makes the pre-existing x/y order deterministic
        // for facts that share a mark, while remaining absent from the emitted schema.
        string source = $"read_parquet('{Sql.Path(companion.FactsParquetPath)}', file_row_number = true)";
        string projection = string.Join(", ", sourceColumns
            .Where(column => column is not TileSchema.Geometry and not TileSchema.PartOffsets and not "ext")
            .Select(column => $"f.{Quote(column)}"));
        long sourceRows = db.Scalar($"SELECT count(*) FROM {source}");
        long targetRows = CompanionBatchTargetRows(companion.FactsParquetPath, sourceRows, resources);
        IReadOnlyList<CompanionFactBatch> batches = PlanCompanionBatches(db, source, sourceRows, targetRows);
        string batchDirectory = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(foldFactsPath))!, $".facts-batches-{Guid.NewGuid():N}");
        Directory.CreateDirectory(batchDirectory);
        long configuredThreads = db.Scalar("SELECT current_setting('threads')");
        long configuredOpenFiles = db.Scalar("SELECT current_setting('partitioned_write_max_open_files')");
        int assemblyRamMb = resources?.RamMb > 0 ? Math.Max(128, resources.RamMb / 5) : 0;

        try
        {
            // Resolve the tiny exact axis domains before range generation fills the filesystem cache. The
            // source scan projects one grain column at a time, so this is bounded and avoids even a small
            // managed allocation at the high-water mark after all range files have been written.
            var axisDomains = SlabPlanner.ScanDomains(
                db, companion, [companion.FactsParquetPath]);
            var batchPaths = new List<string>(batches.Count);
            long leafNnz = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                string factPredicate = batch.Predicate("f");
                string markPredicate = batch.Predicate("t");
                string batchPath = Path.Combine(batchDirectory, $"{i:D5}.parquet");
                batchPaths.Add(batchPath);
                db.Exec($"""
                    COPY (
                      SELECT {projection}, marks.zreal
                      FROM {source} f
                      JOIN (
                        SELECT unhex(substr({Quote(TileSchema.Id)}, 3)) AS geometry_key, zreal
                        FROM t
                        WHERE {markPredicate}
                      ) marks ON f.{Quote(MarkKey.GeometryIdentity)} = marks.geometry_key
                      WHERE {factPredicate}
                      ORDER BY f.{Quote(TileSchema.X)}, f.{Quote(TileSchema.Y)}, f.file_row_number
                    ) TO '{Sql.Path(batchPath)}' (FORMAT PARQUET, PRESERVE_ORDER true)
                    """);
                string grain = string.Concat(companion.GrainChannels.Select(channel => $", {Quote(channel.Name)}"));
                leafNnz += db.Scalar($"SELECT count(DISTINCT ({TileSchema.X}, {TileSchema.Y}{grain})) " +
                                     $"FROM read_parquet('{Sql.Path(batchPath)}')");
            }
            Console.WriteLine($"AggregateReducer wrote {batchPaths.Count} sorted companion ranges.");

            db.Exec("SET preserve_insertion_order = true");
            db.Exec("SET threads = 1");
            db.Exec("SET partitioned_write_max_open_files = 1");
            if (assemblyRamMb > 0) db.Exec($"SET memory_limit = '{assemblyRamMb}MB'");
            IReadOnlyList<string> alignedChunks = sourceRows > 0
                ? AlignParquetRowGroups(db, batchPaths, batchDirectory, sourceRows)
                : batchPaths;
            Console.WriteLine($"AggregateReducer aligned {alignedChunks.Count} Parquet row groups.");
            string chunkList = ParquetList(alignedChunks);
            db.Exec($"""
                COPY (SELECT * FROM read_parquet({chunkList}))
                TO '{Sql.Path(foldFactsPath)}' (FORMAT PARQUET, PRESERVE_ORDER true)
                """);
            Console.WriteLine("AggregateReducer wrote final fold facts Parquet.");
            db.Exec($"CREATE VIEW facts AS SELECT * FROM read_parquet('{Sql.Path(foldFactsPath)}')");

            long loadedRows = db.Scalar("SELECT count(*) FROM facts");
            if (loadedRows != sourceRows)
                throw new InvalidDataException(
                    $"Companion facts matched {loadedRows:N0} of {sourceRows:N0} rows to grouped marks.");
            return new CompanionFactsWriteResult(
                batches.Count, sourceRows, targetRows, leafNnz, axisDomains);
        }
        finally
        {
            db.Exec($"SET partitioned_write_max_open_files = {configuredOpenFiles}");
            db.Exec($"SET threads = {configuredThreads}");
            db.Exec("SET preserve_insertion_order = false");
            if (resources?.DuckDbRamMb > 0) db.Exec($"SET memory_limit = '{resources.DuckDbRamMb}MB'");
            Directory.Delete(batchDirectory, recursive: true);
        }
    }

    private const int ParquetRowGroupRows = 122_880;

    // DuckDB can flush a Parquet row group when an input file ends. Normalize those boundaries without a
    // whole-data table: first split the already ordered range files by their global row-group number, then
    // merge only the (at most one-row-group) fragments for each number. The final writer sees the same row
    // groups whether the expensive stage used one range or many, which is what makes byte identity testable.
    private static IReadOnlyList<string> AlignParquetRowGroups(
        DuckDbSession db,
        IReadOnlyList<string> batchPaths,
        string workDirectory,
        long totalRows)
    {
        string partsRoot = Path.Combine(workDirectory, "row-group-parts");
        string chunksRoot = Path.Combine(workDirectory, "row-groups");
        Directory.CreateDirectory(partsRoot);
        Directory.CreateDirectory(chunksRoot);

        long offset = 0;
        for (int i = 0; i < batchPaths.Count; i++)
        {
            string path = batchPaths[i];
            long rows = db.Scalar($"SELECT count(*) FROM read_parquet('{Sql.Path(path)}')");
            string pattern = $"b{i:D5}_{{uuid}}";
            db.Exec($"""
                COPY (
                  SELECT * EXCLUDE (__row_group), __row_group
                  FROM (
                    SELECT *, (({offset} + row_number() OVER () - 1) / {ParquetRowGroupRows})::BIGINT AS __row_group
                    FROM read_parquet('{Sql.Path(path)}')
                  ) ordered
                ) TO '{Sql.Path(partsRoot)}'
                  (FORMAT PARQUET, PARTITION_BY (__row_group), WRITE_PARTITION_COLUMNS false,
                   APPEND, FILENAME_PATTERN '{pattern}')
                """);
            offset += rows;
        }
        if (offset != totalRows)
            throw new InvalidDataException(
                $"Companion range files contain {offset:N0} rows; expected {totalRows:N0}.");

        int chunkCount = checked((int)((totalRows + ParquetRowGroupRows - 1) / ParquetRowGroupRows));
        var chunks = new List<string>(chunkCount);
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            string partition = Path.Combine(partsRoot, $"__row_group={chunk}");
            string[] fragments = Directory.GetFiles(partition, "*.parquet")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            string output = Path.Combine(chunksRoot, $"{chunk:D8}.parquet");
            db.Exec($"""
                COPY (SELECT * FROM read_parquet({ParquetList(fragments)}))
                TO '{Sql.Path(output)}'
                  (FORMAT PARQUET, ROW_GROUP_SIZE {ParquetRowGroupRows}, PRESERVE_ORDER true)
                """);
            chunks.Add(output);
        }
        return chunks;
    }

    private static string ParquetList(IEnumerable<string> paths) =>
        "[" + string.Join(", ", paths.Select(path => $"'{Sql.Path(path)}'")) + "]";

    private static long CompanionBatchTargetRows(
        string sourcePath, long sourceRows, BakeResourceOptions? resources)
    {
        long ramBytes = resources?.RamBytes ?? 0;
        if (sourceRows <= 0 || ramBytes <= 0) return long.MaxValue;

        // The source is compressed and still contains repeated geometry, so use a conservative expansion
        // rather than treating its on-disk bytes as the sort footprint. One tenth of the total envelope lets
        // a larger machine run proportionally larger ranges while leaving room for the mark hash, DuckDB,
        // the runtime and the filesystem cache. There is no fixed row ceiling.
        long sourceBytes = new FileInfo(sourcePath).Length;
        long estimatedRowBytes = Math.Max(64,
            (long)Math.Ceiling((double)sourceBytes * 4 / Math.Max(1, sourceRows)));
        long rangeBudgetBytes = Math.Max(16L * 1024 * 1024, ramBytes / 10);
        return Math.Max(2_048, rangeBudgetBytes / estimatedRowBytes);
    }

    private static IReadOnlyList<CompanionFactBatch> PlanCompanionBatches(
        DuckDbSession db, string source, long sourceRows, long targetRows)
    {
        if (sourceRows <= targetRows) return [CompanionFactBatch.All];

        double minX = 0, maxX = 0;
        bool hasX;
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT min({TileSchema.X})::DOUBLE, max({TileSchema.X})::DOUBLE FROM {source}";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            hasX = !reader.IsDBNull(0);
            if (hasX)
            {
                minX = reader.GetDouble(0);
                maxX = reader.GetDouble(1);
            }
        }

        long wanted = (sourceRows + targetRows - 1) / targetRows;
        int histogramBins = (int)Math.Min(65_536, Math.Max(2_048, wanted * 256));
        string bucket = BucketSql("f", hasX, minX, maxX, histogramBins);
        var counts = db.LongPairs($"""
            SELECT ({bucket})::BIGINT AS bucket, count(*)::BIGINT
            FROM {source} f
            GROUP BY bucket
            """).ToDictionary(pair => checked((int)pair.Item1), pair => pair.Item2);

        var batches = new List<CompanionFactBatch>();
        int low = 0;
        long rows = 0;
        for (int value = 0; value <= histogramBins; value++)
        {
            long bucketRows = counts.GetValueOrDefault(value);
            if (rows > 0 && bucketRows > 0 && rows + bucketRows > targetRows)
            {
                batches.Add(new CompanionFactBatch(low, value - 1, hasX, minX, maxX, histogramBins));
                low = value;
                rows = 0;
            }
            rows += bucketRows;
        }
        batches.Add(new CompanionFactBatch(low, histogramBins, hasX, minX, maxX, histogramBins));
        return batches;
    }

    private static string BucketSql(string alias, bool hasX, double minX, double maxX, int bins)
    {
        string x = $"{alias}.\"{TileSchema.X}\"";
        if (!hasX || minX == maxX)
            return $"CASE WHEN {x} IS NULL THEN {bins} ELSE 0 END";
        return $"CASE WHEN {x} IS NULL THEN {bins} ELSE " +
               $"least({bins - 1}, greatest(0, CAST(floor((CAST({x} AS DOUBLE) - {Sql.Lit(minX)}) " +
               $"/ {Sql.Lit(maxX - minX)} * {bins}) AS BIGINT))) END";
    }

    internal sealed record CompanionFactsWriteResult(
        int BatchCount,
        long Rows,
        long TargetRows,
        long LeafNnz,
        IReadOnlyDictionary<string, SlabAxisSeed> AxisDomains);

    private sealed record CompanionFactBatch(
        int Low, int High, bool HasX, double MinX, double MaxX, int Bins, bool Whole = false)
    {
        public static CompanionFactBatch All { get; } = new(0, 0, false, 0, 0, 0, Whole: true);

        public string Predicate(string alias) => Whole
            ? "true"
            : $"{BucketSql(alias, HasX, MinX, MaxX, Bins)} BETWEEN {Low} AND {High}";
    }

    private static string[] Measures(ViewConfig view) => view.Source.Channels
        .Where(c => c.Role == ChannelRole.Measure)
        .Select(c => c.Name)
        .ToArray();

    // Group regime only: the dict channels a tile carries alongside geometry — perMark dimensions and
    // argmax measures. Merged sub-pixel cells take the mode; the mark id is handled separately.
    private static string[] DictCarry(ViewConfig view) => view.Source.Channels
        .Where(c => c.Type == ChannelType.Dict && c.Name != TileSchema.Id)
        .Select(c => c.Name)
        .ToArray();

    private static bool HasId(ViewConfig view) => view.Source.Channels.Any(c => c.Name == TileSchema.Id);

    // Loads a parquet and tags each row with zreal = the first zoom whose ~1px grid cell the polygon's
    // extent still exceeds, i.e. the level at which it stops being sub-pixel and becomes real. Used for
    // the marks staging (the pyramid) and, in the group regime, the facts (the companions).
    private static void LoadTagged(
        DuckDbSession db,
        string table,
        string parquetPath,
        Bbox root,
        bool hasShape,
        bool includeGeometryIdentity,
        int pointRealZoom)
    {
        string source = $"read_parquet('{Sql.Path(parquetPath)}')";
        bool hasIdentity = !includeGeometryIdentity || ParquetColumns(db, parquetPath).Contains(MarkKey.GeometryIdentity);
        string identity = includeGeometryIdentity && !hasIdentity
            ? $", {MarkKey.GeometryIdentitySql(hasShape)} AS \"{MarkKey.GeometryIdentity}\""
            : "";

        // Points have no extent array. They stay point marks at every level; their exact source geometry
        // becomes real at the planned depth, while coarser levels use the same sub-pixel grid collapse as
        // shapes. This keeps the public mark representation unchanged.
        if (!hasShape)
        {
            db.Exec($"CREATE TABLE {table} AS SELECT *{identity}, {pointRealZoom}::INTEGER AS zreal FROM {source}");
            return;
        }

        string coordsAt(int parity) =>
            $"list_filter({TileSchema.Geometry}, (v, i) -> i % 2 = {parity})";
        string extent = $"greatest(list_max({coordsAt(1)}) - list_min({coordsAt(1)}), " +
                        $"list_max({coordsAt(0)}) - list_min({coordsAt(0)}))";

        db.Exec($"""
            CREATE TABLE {table} AS
            WITH s AS (
              SELECT *{identity}, {extent} AS ext
              FROM {source}
            )
            SELECT *, CASE
              WHEN ext IS NULL OR ext <= 0 THEN {ZCap}
              ELSE CAST(greatest(0, least({ZCap}, ceil(log2({Sql.Lit(root.SpanX)} / ({TileSchema.GridPerTile} * ext)) - {Sql.Lit(ZSlack)}))) AS INTEGER)
            END AS zreal
            FROM s
            """);
    }

    private static HashSet<string> ParquetColumns(DuckDbSession db, string parquetPath)
        => ParquetColumnList(db, parquetPath).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<string> ParquetColumnList(DuckDbSession db, string parquetPath)
    {
        var columns = new List<string>();
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT column_name FROM (DESCRIBE SELECT * FROM read_parquet('{Sql.Path(parquetPath)}'))";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static void BuildPyramid(DuckDbSession db, ReductionContext ctx,
        List<TileMeta> tiles, ref long leafTotal, SlabCompanionWriter? slab, SlabPlan? plan, bool hasShape)
    {
        if (db.Scalar("SELECT count(*) FROM t") == 0) return;
        int zMax = (int)db.Scalar("SELECT max(zreal) FROM t");

        db.Exec("CREATE TABLE act (tx BIGINT, ty BIGINT)");
        db.Exec("INSERT INTO act VALUES (0, 0)");

        for (int z = 0; z <= zMax; z++)
        {
            string tx = TileSql.TileIndex(ctx.Root, z, Axis.X);
            string ty = TileSql.TileIndex(ctx.Root, z, Axis.Y);

            db.Exec($"""
                CREATE OR REPLACE TABLE internals AS
                SELECT DISTINCT {tx} AS tx, {ty} AS ty
                FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
                WHERE zreal > {z}
                """);
            var internals = db.LongPairs("SELECT tx, ty FROM internals");

            string contentSql = ContentSql(ctx.Root, z, ctx.View, ctx.GroupRegime, hasShape);
            Func<long, long, string> tilePath =
                (wtx, wty) => Path.Combine(ctx.OutputDirectory, new TileId(z, (int)wtx, (int)wty).RelativePath);

            List<(long Tx, long Ty, long Rows)> written;
            if (ctx.Companion is { } companion && slab is not null && plan is not null)
            {
                // Materialize the level's content with each mark's row index within its tile, so the
                // companion addresses marks by `mki` — the client's O(1) join to the rendered mark (no
                // string keys). The within-tile order (ORDER BY id) only has to match between the writes.
                db.Exec($"""
                    CREATE OR REPLACE TABLE content AS
                    SELECT *, (row_number() OVER (PARTITION BY tx, ty ORDER BY "{TileSchema.Id}") - 1)::INTEGER AS mki
                    FROM ({contentSql})
                    """);
                Console.WriteLine($"AggregateReducer z{z}: render content ready.");
                written = ArrowTileWriter.WritePartitioned(db.Connection,
                    "SELECT * EXCLUDE (mki) FROM content ORDER BY tx, ty, mki", tilePath,
                    ctx.View.DictionaryEncodedChannels(), ctx.CanonicalDictionaryOrders, ctx.TileFormat);
                Console.WriteLine($"AggregateReducer z{z}: {written.Count} render tiles written.");

                // Companions ride the same active tiles (facts share their marks' (tx,ty)). Each tile's
                // slab — CSR or dense cumulative planes at grain, keyed by mki — is appended to facts.pack.
                var markCounts = written.ToDictionary(w => (w.Tx, w.Ty), w => w.Rows);
                slab.AppendLevel(z, db.Connection, SlabCompanionSql(ctx.Root, z, companion, plan), markCounts);
                Console.WriteLine($"AggregateReducer z{z}: companion slabs written.");
            }
            else
            {
                written = ArrowTileWriter.WritePartitioned(db.Connection, contentSql, tilePath,
                    ctx.View.DictionaryEncodedChannels(), ctx.CanonicalDictionaryOrders, ctx.TileFormat);
            }

            foreach (var (wtx, wty, rows) in written)
            {
                bool isLeaf = !internals.Contains((wtx, wty));
                tiles.Add(new TileMeta(z, (int)wtx, (int)wty, rows, isLeaf));
                if (isLeaf) leafTotal += rows;
            }

            if (internals.Count == 0) break;
            db.Exec($"""
                CREATE OR REPLACE TABLE act AS
                SELECT DISTINCT {TileSql.TileIndex(ctx.Root, z + 1, Axis.X)} AS tx,
                                {TileSql.TileIndex(ctx.Root, z + 1, Axis.Y)} AS ty
                FROM t JOIN internals ON {tx} = internals.tx AND {ty} = internals.ty
                """);
        }
    }

    // Rows at this level, partitioned by (tx, ty): real polygons pass through untouched; sub-pixel rows
    // collapse to one synthetic ~1px cell per occupied grid square, averaging each measure. In the group
    // regime the mark id and dict channels ride along too — a merged cell takes the grid key as its id
    // (matching its fact companion, MarkKey) and the mode of each dict channel.
    private static string ContentSql(Bbox root, int z, ViewConfig view, bool groupRegime, bool hasShape)
    {
        string tx = TileSql.TileIndex(root, z, Axis.X);
        string ty = TileSql.TileIndex(root, z, Axis.Y);
        string gx = TileSql.GridIndex(root, z, Axis.X);
        string gy = TileSql.GridIndex(root, z, Axis.Y);

        string x0 = TileSql.GridCellMin(root, z, Axis.X, "gx"), x1 = TileSql.GridCellMin(root, z, Axis.X, "gx + 1");
        string y0 = TileSql.GridCellMin(root, z, Axis.Y, "gy"), y1 = TileSql.GridCellMin(root, z, Axis.Y, "gy + 1");
        double halfX = TileSql.GridCellSize(root, z, Axis.X) / 2, halfY = TileSql.GridCellSize(root, z, Axis.Y) / 2;
        string ring = $"[{x0}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y0}::FLOAT, {x1}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y1}::FLOAT, {x0}::FLOAT, {y0}::FLOAT]";

        string[] measures = Measures(view);
        // f32 with null → NaN so the tile buffer is view-safe on the client (format 2): no null bitmap,
        // no cast. avg() of an all-null group would otherwise be NULL.
        string measReal = string.Concat(measures.Select(m => $", COALESCE(\"{m}\"::FLOAT, 'nan'::FLOAT) AS \"{m}\""));
        string measAvg = string.Concat(measures.Select(m => $", COALESCE(avg(\"{m}\")::FLOAT, 'nan'::FLOAT) AS \"{m}\""));

        string idReal = "", idMerged = "", dictReal = "", dictMerged = "";
        if (groupRegime)
        {
            if (HasId(view))
            {
                idReal = $", \"{TileSchema.Id}\"";
                idMerged = $", {MarkKey.MergedSql("gx", "gy")} AS \"{TileSchema.Id}\"";
            }
            string[] dicts = DictCarry(view);
            dictReal = string.Concat(dicts.Select(d => $", \"{d}\""));
            dictMerged = string.Concat(dicts.Select(d => $", mode(\"{d}\") AS \"{d}\""));
        }

        string geometryReal = hasShape ? $", {TileSchema.Geometry}, {TileSchema.PartOffsets}" : "";
        string geometryMerged = hasShape
            ? $", {ring} AS {TileSchema.Geometry}, [0, 5]::INTEGER[] AS {TileSchema.PartOffsets}"
            : "";

        return $"""
            WITH v AS (
              SELECT t.*, {tx} AS tx, {ty} AS ty, {gx} AS gx, {gy} AS gy
              FROM t JOIN act ON {tx} = act.tx AND {ty} = act.ty
            )
            SELECT tx, ty, {TileSchema.X}::FLOAT AS {TileSchema.X}, {TileSchema.Y}::FLOAT AS {TileSchema.Y}
                   {geometryReal}{idReal}{measReal}{dictReal}
            FROM v WHERE zreal <= {z}
            UNION ALL
            SELECT tx, ty,
                   ({x0} + {Sql.Lit(halfX)})::FLOAT AS {TileSchema.X},
                   ({y0} + {Sql.Lit(halfY)})::FLOAT AS {TileSchema.Y}
                   {geometryMerged}{idMerged}{measAvg}{dictMerged}
            FROM v WHERE zreal > {z}
            GROUP BY tx, ty, gx, gy
            ORDER BY tx, ty
            """;
    }

    // Fact partials at grain for this level, partitioned by (tx, ty), projected to the slab's stream form
    // (tx, ty, mki, cellId, partials…). Each fact keys to its mark — the real geometry key while the mark
    // is real (zreal ≤ z), else the grid-cell key it merged into — then joins the materialized content to
    // carry `mki`; the grain values fold into the canonical `cellId`. Ordered (tx, ty, mki, cellId) so the
    // writer streams marks in order with ascending cellId per mark (the CSR / cell-major order).
    private static string SlabCompanionSql(Bbox root, int z, CompanionSpec c, SlabPlan plan)
    {
        string tx = TileSql.TileIndex(root, z, Axis.X);
        string ty = TileSql.TileIndex(root, z, Axis.Y);
        string gx = TileSql.GridIndex(root, z, Axis.X);
        string gy = TileSql.GridIndex(root, z, Axis.Y);
        string mk = $"CASE WHEN zreal <= {z} THEN {MarkKey.RealSql()} ELSE {MarkKey.MergedSql("gx", "gy")} END";
        string grain = string.Concat(c.GrainChannels.Select(ch => $", \"{ch.Name}\""));
        // The plan's partial set (which may add cnt for a dense witness) drives the columns, so the writer's
        // plan.Partials and this projection stay in lockstep.
        string partials = string.Concat(plan.Partials.Select(p => $", {PartialSql(p)} AS \"{p.Name}\""));
        string partialsOut = string.Concat(plan.Partials.Select(p => $", g.\"{p.Name}\""));

        return $"""
            WITH vf AS (
              SELECT facts.*, {gx} AS gx, {gy} AS gy, {tx} AS tx, {ty} AS ty
              FROM facts JOIN act ON {tx} = act.tx AND {ty} = act.ty
            ),
            g AS (
              SELECT tx, ty, ({mk}) AS mk{grain}{partials}
              FROM vf
              GROUP BY ALL
            )
            SELECT g.tx, g.ty, content.mki, ({plan.CellIdSql("g")}) AS cellId{partialsOut}
            FROM g JOIN content ON content.tx = g.tx AND content.ty = g.ty AND content."{TileSchema.Id}" = g.mk
            ORDER BY g.tx, g.ty, content.mki, cellId
            """;
    }

    private static string PartialSql(Partial p) => p.Kind switch
    {
        PartialKind.Sum => $"COALESCE(sum(\"{p.Channel}\"), 0)::FLOAT",
        PartialKind.Count => "count(*)::INTEGER",
        PartialKind.Swp => $"COALESCE(sum(\"{p.Channel}\" * \"{p.Weight}\"), 0)::FLOAT",
        PartialKind.Min => $"min(\"{p.Channel}\")::FLOAT",
        PartialKind.Max => $"max(\"{p.Channel}\")::FLOAT",
        _ => throw new InvalidOperationException($"unhandled partial {p.Kind}"),
    };
}
