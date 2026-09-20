using Colossus.NumericFilterBench;

if (args.Length == 0)
{
    Usage();
    return 2;
}

try
{
    switch (args[0])
    {
        case "inspect" when args.Length == 2:
            await DuckDbTasks.InspectAsync(Path.GetFullPath(args[1]));
            break;
        case "profile" when args.Length == 2:
            await DuckDbTasks.ProfileAsync(Path.GetFullPath(args[1]));
            break;
        case "prepare-timestamp-sorted-shard" when args.Length == 6:
            await DuckDbTasks.PrepareTimestampSortedShardAsync(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3],
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "verify-timestamp-cell" when args.Length == 4:
            await DuckDbTasks.VerifyTimestampCellAsync(
                Path.GetFullPath(args[1]), args[2],
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "build" when args.Length == 3:
            await ArtifactBuilder.BuildAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            break;
        case "bench" when args.Length is 2 or 3:
            BenchmarkRunner.Run(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "all");
            break;
        case "bench-once" when args.Length == 4:
            BenchmarkRunner.RunOnce(Path.GetFullPath(args[1]), args[2], args[3]);
            break;
        case "pressure" when args.Length is >= 3 and <= 5:
            BenchmarkRunner.Pressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                args.Length >= 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 5,
                args.Length == 5 ? args[4] : "cover");
            break;
        case "initial-pressure" when args.Length is >= 3 and <= 5:
            BenchmarkRunner.InitialPressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                args.Length >= 4 ? args[3] : "broad",
                args.Length == 5 ? args[4] : "cover");
            break;
        case "duckdb-build" when args.Length == 4:
            await DuckDbTasks.BuildServingDatabaseAsync(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), bool.Parse(args[3]));
            break;
        case "duckdb-bench" when args.Length == 3:
            await DuckDbTasks.BenchmarkServingDatabaseAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            break;
        case "duckdb-pressure" when args.Length == 4:
            await DuckDbTasks.PressureServingDatabaseAsync(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "datafusion-bench" when args.Length == 3:
            NativeDataFusion.Benchmark(Path.GetFullPath(args[1]), args[2]);
            break;
        case "datafusion-pressure" when args.Length == 4:
            NativeDataFusion.Pressure(
                Path.GetFullPath(args[1]), args[2],
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-build" when args.Length == 4:
            NativeLance.Build(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-index" when args.Length == 3:
            NativeLance.CreateIndexes(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-bench" when args.Length == 5:
            NativeLance.Benchmark(
                Path.GetFullPath(args[1]), args[2],
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-shard-build" when args.Length == 6:
            NativeLance.BuildShard(
                Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[3],
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-shard-index" when args.Length == 3:
            NativeLance.CreateShardIndex(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-shard-bench" when args.Length == 4:
            NativeLance.BenchmarkShard(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-shard-pressure" when args.Length is 5 or 6:
            NativeLance.PressureShard(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                args.Length == 6
                    ? int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture)
                    : int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "lance-shard-serve-e2e" when args.Length == 7:
            await LanceE2eServer.RunAsync(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[6], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "serve-e2e" when args.Length is >= 3 and <= 5:
            await E2eServer.RunAsync(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                args.Length == 3 || args[3] switch
                {
                    "brotli" => true,
                    "plain" => false,
                    _ => throw new ArgumentException("E2E response mode must be brotli or plain")
                },
                args.Length == 5 && args[4] switch
                {
                    "stateless" => true,
                    "stateful" => false,
                    _ => throw new ArgumentException("E2E engine mode must be stateful or stateless")
                });
            break;
        case "synthetic-build" when args.Length == 4:
            SyntheticBillionBuilder.Build(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "synthetic-bench" when args.Length == 3:
            SyntheticBillionRunner.Run(
                Path.GetFullPath(args[1]),
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "synthetic-bench-superset" when args.Length == 3:
            SyntheticBillionRunner.Run(
                Path.GetFullPath(args[1]),
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                boundarySuperset: true);
            break;
        case "synthetic-pressure" when args.Length == 5:
            SyntheticBillionRunner.Pressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                args[4] switch
                {
                    "distinct" => true,
                    "same" => false,
                    _ => throw new ArgumentException("Synthetic pressure ranges must be distinct or same")
                });
            break;
        case "synthetic-pressure-superset" when args.Length == 5:
            SyntheticBillionRunner.Pressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                args[4] switch
                {
                    "distinct" => true,
                    "same" => false,
                    _ => throw new ArgumentException("Synthetic pressure ranges must be distinct or same")
                },
                boundarySuperset: true);
            break;
        case "synthetic-cover-build" when args.Length == 2:
            SyntheticStatefulCoverBuilder.Build(Path.GetFullPath(args[1]));
            break;
        case "synthetic-cover-bench" when args.Length == 3:
            SyntheticStatefulCoverRunner.Run(
                Path.GetFullPath(args[1]),
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "synthetic-cover-pressure" when args.Length == 5:
            SyntheticStatefulCoverRunner.Pressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                args[4] switch
                {
                    "distinct" => true,
                    "same" => false,
                    _ => throw new ArgumentException("Synthetic cover pressure ranges must be distinct or same")
                });
            break;
        case "synthetic-cover-first-hit" when args.Length == 3:
            SyntheticStatefulCoverRunner.FirstHit(
                Path.GetFullPath(args[1]),
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "synthetic-cover-first-hit-pressure" when args.Length == 4:
            SyntheticStatefulCoverRunner.FirstHitPressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "timestamp-tree-build" when args.Length == 5:
            TimestampTreeBuilder.Build(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "timestamp-tree-bench" when args.Length is 2 or 3:
            TimestampTreeRunner.Run(
                Path.GetFullPath(args[1]),
                args.Length == 3 ? args[2] : "all");
            break;
        case "timestamp-tree-pressure" when args.Length is >= 3 and <= 5:
            TimestampTreeRunner.Pressure(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                args.Length == 4
                    ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
                    : args.Length == 5
                        ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
                        : 6,
                args.Length == 5 && args[4] switch
                {
                    "direct" => true,
                    "buffered" => false,
                    _ => throw new ArgumentException("Timestamp pressure I/O mode must be buffered or direct")
                });
            break;
        case "timestamp-cache-pollute" when args.Length == 3:
            TimestampCachePolluter.CreateAndRead(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            break;
        case "cache-pollute-read" when args.Length == 2:
            TimestampCachePolluter.ReadExisting(Path.GetFullPath(args[1]));
            break;
        case "timestamp-tree-serve-e2e" when args.Length is 3 or 4:
            await TimestampTreeE2eServer.RunAsync(
                Path.GetFullPath(args[1]),
                int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
                args.Length == 4 && args[3] switch
                {
                    "direct" => true,
                    "buffered" => false,
                    _ => throw new ArgumentException("Timestamp E2E I/O mode must be buffered or direct")
                });
            break;
        default:
            Usage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

return 0;

static void Usage()
{
    Console.Error.WriteLine("""
        Colossus.NumericFilterBench commands:
          inspect <facts.parquet>
          profile <facts.parquet>
          prepare-timestamp-sorted-shard <facts.parquet> <output.parquet> <operator> <quarter> <threads>
          verify-timestamp-cell <facts.parquet> <operator> <quarter>
          build <facts.parquet> <artifact-dir>
          bench <artifact-dir> [all|scan|mark|index|bitmap|cover|index-delta|bitmap-delta|delta|small-delta|stateless-delta]
          bench-once <artifact-dir> <scan|mark|index|bitmap|cover> <q01-q99|q05-q95|q25-q75|q45-q55>
          pressure <artifact-dir> <users> [seconds] [cover|cover-encode|cover-brotli|cover-varint|cover-varint-brotli|stateless-varint|stateless-index-varint|index|bitmap]
          initial-pressure <artifact-dir> <users> [broad|middle] [cover|bitmap]
          duckdb-build <facts.parquet> <serving.duckdb> <create-art-indexes:true|false>
          duckdb-bench <serving.duckdb> <artifact-dir>
          duckdb-pressure <serving.duckdb> <artifact-dir> <users>
          datafusion-bench <artifact-dir> <scan|driver|both>
          datafusion-pressure <artifact-dir> <scan|driver> <users>
          lance-build <facts.parquet> <dataset-dir> <threads>
          lance-index <dataset-dir> <threads>
          lance-bench <dataset-dir> <operator> <quarter> <threads>
          lance-shard-build <facts.parquet> <shard-dir> <operator> <quarter> <threads>
          lance-shard-index <shard-dir> <threads>
          lance-shard-bench <shard-dir> <quarter> <threads>
          lance-shard-pressure <shard-dir> <quarter> <threads> <users> [max-active]
          lance-shard-serve-e2e <shard-dir> <quarter> <port> <threads> <marks> <max-active>
          serve-e2e <artifact-dir> <port> [brotli|plain] [stateful|stateless]
          synthetic-build <artifact-dir> <facts> <marks>
          synthetic-bench <artifact-dir> <movement-fraction>
          synthetic-bench-superset <artifact-dir> <movement-fraction>
          synthetic-pressure <artifact-dir> <users> <movement-fraction> <distinct|same>
          synthetic-pressure-superset <artifact-dir> <users> <movement-fraction> <distinct|same>
          synthetic-cover-build <artifact-dir>
          synthetic-cover-bench <artifact-dir> <movement-fraction>
          synthetic-cover-pressure <artifact-dir> <users> <movement-fraction> <distinct|same>
          synthetic-cover-first-hit <artifact-dir> <range-width>
          synthetic-cover-first-hit-pressure <artifact-dir> <users> <range-width>
          timestamp-tree-build <artifact-dir> <facts> <marks> <days>
          timestamp-tree-bench <artifact-dir> [all|worst-absolute|worst-absolute-direct|disjoint-delta|disjoint-delta-direct]
          timestamp-tree-pressure <artifact-dir> <users> [max-active] [buffered|direct]
          timestamp-cache-pollute <file> <gib>
          cache-pollute-read <existing-file>
          timestamp-tree-serve-e2e <artifact-dir> <port> [buffered|direct]
        """);
}
