using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Colossus.NumericFilterBench;

internal static unsafe class NativeLance
{
    private const long QuarterSeconds = 7_889_400;

    public static void Build(string parquet, string directory, int threads)
    {
        ProcessMetrics.PinToSixCores();
        nint library = NativeLibrary.Load(LibraryPath());
        var build = Load<BuildDelegate>(library, "colossus_lance_build");
        nint parquetUtf8 = Marshal.StringToCoTaskMemUTF8(parquet);
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        try
        {
            MeasuredAction outcome = Measure(() =>
            {
                int status = build(parquetUtf8, directoryUtf8, threads);
                if (status != 0)
                    throw new InvalidOperationException($"Lance build failed with native status {status}");
            });
            Console.WriteLine(
                $"operation=lance-build threads={threads} wallMs={outcome.WallMs:F2} " +
                $"cpuMs={outcome.CpuMs:F2} effectiveCores={outcome.EffectiveCores:F2} " +
                $"processReadMiB={outcome.ReadBytes / 1048576.0:F2} " +
                $"processWriteMiB={outcome.WriteBytes / 1048576.0:F2} " +
                $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
                $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
                $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2} " +
                $"pageFaults={outcome.PageFaults} artifactMiB={DirectoryBytes(directory) / 1048576.0:F2}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(parquetUtf8);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void BuildShard(
        string parquet, string directory, string operatorValue, int quarter, int threads)
    {
        ProcessMetrics.PinToSixCores();
        nint library = NativeLibrary.Load(LibraryPath());
        var build = Load<BuildShardDelegate>(library, "colossus_lance_build_shard");
        nint parquetUtf8 = Marshal.StringToCoTaskMemUTF8(parquet);
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint operatorUtf8 = Marshal.StringToCoTaskMemUTF8(operatorValue);
        try
        {
            MeasuredAction outcome = Measure(() =>
            {
                int status = build(parquetUtf8, directoryUtf8, operatorUtf8, quarter, threads);
                if (status != 0)
                    throw new InvalidOperationException($"Lance shard build failed with native status {status}");
            });
            Console.WriteLine(
                $"operation=lance-shard-build operator={operatorValue} quarter={quarter} threads={threads} " +
                $"wallMs={outcome.WallMs:F2} cpuMs={outcome.CpuMs:F2} " +
                $"effectiveCores={outcome.EffectiveCores:F2} " +
                $"processReadMiB={outcome.ReadBytes / 1048576.0:F2} " +
                $"processWriteMiB={outcome.WriteBytes / 1048576.0:F2} " +
                $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
                $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
                $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2} " +
                $"pageFaults={outcome.PageFaults} artifactMiB={DirectoryBytes(directory) / 1048576.0:F2}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(operatorUtf8);
            Marshal.FreeCoTaskMem(parquetUtf8);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void CreateIndexes(string directory, int threads)
    {
        ProcessMetrics.PinToSixCores();
        long before = DirectoryBytes(directory);
        nint library = NativeLibrary.Load(LibraryPath());
        var create = Load<CreateIndexesDelegate>(library, "colossus_lance_create_indexes");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        try
        {
            MeasuredAction outcome = Measure(() =>
            {
                int status = create(directoryUtf8, threads);
                if (status != 0)
                    throw new InvalidOperationException($"Lance index build failed with native status {status}");
            });
            long after = DirectoryBytes(directory);
            Console.WriteLine(
                $"operation=lance-index-build threads={threads} wallMs={outcome.WallMs:F2} " +
                $"cpuMs={outcome.CpuMs:F2} effectiveCores={outcome.EffectiveCores:F2} " +
                $"processReadMiB={outcome.ReadBytes / 1048576.0:F2} " +
                $"processWriteMiB={outcome.WriteBytes / 1048576.0:F2} " +
                $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
                $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
                $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2} " +
                $"pageFaults={outcome.PageFaults} baseMiB={before / 1048576.0:F2} " +
                $"indexMiB={(after - before) / 1048576.0:F2} totalMiB={after / 1048576.0:F2}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void CreateShardIndex(string directory, int threads)
    {
        ProcessMetrics.PinToSixCores();
        long before = DirectoryBytes(directory);
        nint library = NativeLibrary.Load(LibraryPath());
        var create = Load<CreateIndexesDelegate>(library, "colossus_lance_create_shard_index");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        try
        {
            MeasuredAction outcome = Measure(() =>
            {
                int status = create(directoryUtf8, threads);
                if (status != 0)
                    throw new InvalidOperationException($"Lance shard index build failed with native status {status}");
            });
            long after = DirectoryBytes(directory);
            Console.WriteLine(
                $"operation=lance-shard-index-build threads={threads} wallMs={outcome.WallMs:F2} " +
                $"cpuMs={outcome.CpuMs:F2} effectiveCores={outcome.EffectiveCores:F2} " +
                $"processReadMiB={outcome.ReadBytes / 1048576.0:F2} " +
                $"processWriteMiB={outcome.WriteBytes / 1048576.0:F2} " +
                $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
                $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
                $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2} " +
                $"pageFaults={outcome.PageFaults} baseMiB={before / 1048576.0:F2} " +
                $"indexMiB={(after - before) / 1048576.0:F2} totalMiB={after / 1048576.0:F2}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void Benchmark(string directory, string operatorValue, int quarter, int threads)
    {
        ProcessMetrics.PinToSixCores();
        nint library = NativeLibrary.Load(LibraryPath());
        var open = Load<OpenDelegate>(library, "colossus_lance_open");
        var query = Load<QueryDelegate>(library, "colossus_lance_query");
        var close = Load<CloseDelegate>(library, "colossus_lance_close");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint operatorUtf8 = Marshal.StringToCoTaskMemUTF8(operatorValue);
        nint engine = 0;
        try
        {
            engine = open(directoryUtf8, threads);
            if (engine == 0)
                throw new InvalidOperationException("Lance + DataFusion failed to open the dataset");

            long quarterLow = quarter * QuarterSeconds;
            long quarterHigh = checked((quarter + 1L) * QuarterSeconds - 1);
            long domainHigh = long.MaxValue;
            LanceCase[] cases =
            [
                new("all-cells-wide", null, -1, 123, domainHigh - 456),
                new("one-operator-wide", operatorValue, -1, 123, domainHigh - 456),
                new("quarter-all-operators", null, quarter, quarterLow + 123, quarterHigh - 456),
                new("one-cell-wide", operatorValue, quarter, quarterLow + 123, quarterHigh - 456),
                new("one-cell-middle", operatorValue, quarter,
                    quarterLow + QuarterSeconds / 4, quarterLow + QuarterSeconds * 3 / 4)
            ];

            foreach (LanceCase item in cases)
            {
                QueryOutcome first = Invoke(
                    query, engine, item, item.Name == "one-cell-wide", operatorUtf8);
                Print(item.Name, "first", threads, first, null);
                QueryOutcome repeat = Invoke(query, engine, item, false, operatorUtf8);
                bool exact = first.Metrics.Rows == repeat.Metrics.Rows &&
                             first.Metrics.Checksum == repeat.Metrics.Checksum;
                Print(item.Name, "repeat", threads, repeat, exact);
            }
        }
        finally
        {
            if (engine != 0) close(engine);
            Marshal.FreeCoTaskMem(operatorUtf8);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void BenchmarkShard(string directory, int quarter, int threads)
    {
        ProcessMetrics.PinToSixCores();
        nint library = NativeLibrary.Load(LibraryPath());
        var open = Load<OpenDelegate>(library, "colossus_lance_open");
        var query = Load<QueryDelegate>(library, "colossus_lance_query");
        var close = Load<CloseDelegate>(library, "colossus_lance_close");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint engine = 0;
        try
        {
            engine = open(directoryUtf8, threads);
            if (engine == 0)
                throw new InvalidOperationException("Lance + DataFusion failed to open the shard");

            long quarterLow = quarter * QuarterSeconds;
            long quarterHigh = checked((quarter + 1L) * QuarterSeconds - 1);
            LanceCase[] cases =
            [
                new("shard-wide", null, -1, quarterLow + 123, quarterHigh - 456),
                new("shard-middle", null, -1,
                    quarterLow + QuarterSeconds / 4, quarterLow + QuarterSeconds * 3 / 4)
            ];

            foreach (LanceCase item in cases)
            {
                QueryOutcome first = Invoke(query, engine, item, item.Name == "shard-wide", 0);
                Print(item.Name, "first", threads, first, null, "lance-shard-datafusion");
                QueryOutcome repeat = Invoke(query, engine, item, false, 0);
                bool exact = first.Metrics.Rows == repeat.Metrics.Rows &&
                             first.Metrics.Checksum == repeat.Metrics.Checksum;
                Print(item.Name, "repeat", threads, repeat, exact, "lance-shard-datafusion");
            }
        }
        finally
        {
            if (engine != 0) close(engine);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    public static void PressureShard(
        string directory, int quarter, int threads, int users, int maxActive)
    {
        if (users <= 0) throw new ArgumentOutOfRangeException(nameof(users));
        if (maxActive <= 0 || maxActive > users) throw new ArgumentOutOfRangeException(nameof(maxActive));
        ProcessMetrics.PinToSixCores();
        nint library = NativeLibrary.Load(LibraryPath());
        var open = Load<OpenDelegate>(library, "colossus_lance_open");
        var query = Load<QueryDelegate>(library, "colossus_lance_query");
        var close = Load<CloseDelegate>(library, "colossus_lance_close");
        nint directoryUtf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint engine = 0;
        try
        {
            engine = open(directoryUtf8, threads);
            if (engine == 0)
                throw new InvalidOperationException("Lance + DataFusion failed to open the shard");

            long quarterLow = quarter * QuarterSeconds;
            long quarterHigh = checked((quarter + 1L) * QuarterSeconds - 1);
            LanceCase warm = new("warm", null, -1, quarterLow + 123, quarterHigh - 456);
            _ = Invoke(query, engine, warm, false, 0);

            var barrier = new Barrier(users + 1);
            var latency = new double[users];
            var rowCounts = new ulong[users];
            var checksums = new ulong[users];
            var failures = new Exception?[users];
            var workers = new Thread[users];
            using var concurrency = new SemaphoreSlim(maxActive, maxActive);
            for (int user = 0; user < users; user++)
            {
                int local = user;
                workers[user] = new Thread(() =>
                {
                    try
                    {
                        long movable = QuarterSeconds / 20;
                        long offset = movable * (local + 1L) / (users + 1L);
                        LanceCase item = new(
                            $"pressure-{local}", null, -1,
                            quarterLow + offset,
                            quarterHigh - (movable - offset));
                        barrier.SignalAndWait();
                        var wall = Stopwatch.StartNew();
                        concurrency.Wait();
                        NativeMetrics metrics;
                        try
                        {
                            metrics = InvokeNative(query, engine, item);
                        }
                        finally
                        {
                            concurrency.Release();
                        }
                        wall.Stop();
                        latency[local] = wall.Elapsed.TotalMilliseconds;
                        rowCounts[local] = metrics.Rows;
                        checksums[local] = metrics.Checksum;
                    }
                    catch (Exception error)
                    {
                        failures[local] = error;
                    }
                }) { IsBackground = true, Name = $"lance-shard-user-{user}" };
                workers[user].Start();
            }

            var process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpu0 = process.TotalProcessorTime;
            long ws0 = process.WorkingSet64;
            (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
            int faults0 = ProcessMetrics.PageFaults();
            long sampledPeak = ws0;
            using var sampling = new CancellationTokenSource();
            Task sampler = Task.Run(() =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    process.Refresh();
                    sampledPeak = Math.Max(sampledPeak, process.WorkingSet64);
                    Thread.Sleep(5);
                }
            });

            barrier.SignalAndWait();
            var burst = Stopwatch.StartNew();
            foreach (Thread worker in workers) worker.Join();
            burst.Stop();
            sampling.Cancel();
            sampler.GetAwaiter().GetResult();
            process.Refresh();
            sampledPeak = Math.Max(sampledPeak, process.WorkingSet64);
            (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
            double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;

            Exception? failure = failures.FirstOrDefault(item => item is not null);
            if (failure is not null) throw new AggregateException(failures.Where(item => item is not null)!);
            Array.Sort(latency);
            double p50 = Percentile(latency, 0.50);
            double p95 = Percentile(latency, 0.95);
            Console.WriteLine(
                $"engine=lance-shard-datafusion operation=pressure users={users} maxActive={maxActive} threads={threads} " +
                $"wallMs={burst.Elapsed.TotalMilliseconds:F2} throughputQps={users / burst.Elapsed.TotalSeconds:F2} " +
                $"latencyP50Ms={p50:F2} latencyP95Ms={p95:F2} latencyMaxMs={latency[^1]:F2} " +
                $"cpuMs={cpuMs:F2} effectiveCores={cpuMs / burst.Elapsed.TotalMilliseconds:F2} " +
                $"allocatedLogicalCpuPercent={cpuMs / burst.Elapsed.TotalMilliseconds / threads * 100.0:F1} " +
                $"processReadMiB={(read1 - read0) / 1048576.0:F2} " +
                $"processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
                $"workingSetStartMiB={ws0 / 1048576.0:F2} " +
                $"workingSetEndMiB={process.WorkingSet64 / 1048576.0:F2} " +
                $"sampledPeakWorkingSetMiB={sampledPeak / 1048576.0:F2} " +
                $"pageFaults={ProcessMetrics.PageFaults() - faults0} " +
                $"totalRows={rowCounts.Aggregate(0UL, (sum, value) => sum + value)} " +
                $"combinedChecksum=0x{checksums.Aggregate(0UL, (hash, value) => hash ^ value):X16}");
        }
        finally
        {
            if (engine != 0) close(engine);
            Marshal.FreeCoTaskMem(directoryUtf8);
            NativeLibrary.Free(library);
        }
    }

    private static NativeMetrics InvokeNative(QueryDelegate query, nint engine, LanceCase item)
    {
        NativeMetrics metrics = default;
        int status = query(
            engine, 0, item.Quarter, item.LowTimestamp, item.HighTimestamp, 0, &metrics);
        if (status != 0)
            throw new InvalidOperationException($"Lance query failed with native status {status}");
        return metrics;
    }

    private static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(sorted.Length * quantile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static QueryOutcome Invoke(
        QueryDelegate query, nint engine, LanceCase item, bool explain, nint operatorUtf8)
    {
        NativeMetrics metrics = default;
        nint selectedOperator = item.Operator is null ? 0 : operatorUtf8;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        int faults0 = ProcessMetrics.PageFaults();
        var wall = Stopwatch.StartNew();
        int status = query(
            engine, selectedOperator, item.Quarter, item.LowTimestamp, item.HighTimestamp,
            explain ? 1 : 0, &metrics);
        wall.Stop();
        if (status != 0)
            throw new InvalidOperationException($"Lance query failed with native status {status}");
        process.Refresh();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        return new QueryOutcome(
            metrics,
            wall.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpu0).TotalMilliseconds,
            process.WorkingSet64,
            process.WorkingSet64 - ws0,
            process.PeakWorkingSet64,
            read1 - read0,
            write1 - write0,
            ProcessMetrics.PageFaults() - faults0);
    }

    private static void Print(
        string name, string pass, int threads, QueryOutcome outcome, bool? exact,
        string engine = "lance-datafusion")
    {
        string exactText = exact is null ? "" : $" repeatExact={exact.Value.ToString().ToLowerInvariant()}";
        Console.WriteLine(
            $"engine={engine} query={name} pass={pass} threads={threads} " +
            $"wallMs={outcome.WallMs:F2} engineMs={outcome.Metrics.EngineMilliseconds:F2} " +
            $"cpuMs={outcome.CpuMs:F2} effectiveCores={outcome.CpuMs / outcome.WallMs:F2} " +
            $"rows={outcome.Metrics.Rows} checksum=0x{outcome.Metrics.Checksum:X16} " +
            $"rowsScanned={outcome.Metrics.RowsScanned} fragmentsScanned={outcome.Metrics.FragmentsScanned} " +
            $"rangesScanned={outcome.Metrics.RangesScanned} lanceReadMiB={outcome.Metrics.BytesRead / 1048576.0:F2} " +
            $"indicesLoaded={outcome.Metrics.IndicesLoaded} indexPartsLoaded={outcome.Metrics.IndexPartsLoaded} " +
            $"indexComparisons={outcome.Metrics.IndexComparisons} indexCacheHits={outcome.Metrics.IndexCacheHits} " +
            $"indexCacheMisses={outcome.Metrics.IndexCacheMisses} " +
            $"processReadMiB={outcome.ReadBytes / 1048576.0:F2} " +
            $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
            $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
            $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2} pageFaults={outcome.PageFaults}" +
            exactText);
    }

    private static MeasuredAction Measure(Action action)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        int faults0 = ProcessMetrics.PageFaults();
        var wall = Stopwatch.StartNew();
        action();
        wall.Stop();
        process.Refresh();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        return new MeasuredAction(
            wall.Elapsed.TotalMilliseconds,
            cpuMs,
            cpuMs / wall.Elapsed.TotalMilliseconds,
            process.WorkingSet64,
            process.WorkingSet64 - ws0,
            process.PeakWorkingSet64,
            read1 - read0,
            write1 - write0,
            ProcessMetrics.PageFaults() - faults0);
    }

    private static long DirectoryBytes(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
        : 0;

    private static string LibraryPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "native", "target", "release",
        OperatingSystem.IsWindows() ? "colossus_datafusion_bench.dll" : "libcolossus_datafusion_bench.so"));

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMetrics
    {
        public double EngineMilliseconds;
        public ulong Rows;
        public ulong Checksum;
        public ulong RowsScanned;
        public ulong FragmentsScanned;
        public ulong RangesScanned;
        public ulong BytesRead;
        public ulong IndicesLoaded;
        public ulong IndexPartsLoaded;
        public ulong IndexComparisons;
        public ulong IndexCacheHits;
        public ulong IndexCacheMisses;
    }

    private sealed record LanceCase(
        string Name, string? Operator, int Quarter, long LowTimestamp, long HighTimestamp);

    private sealed record QueryOutcome(
        NativeMetrics Metrics, double WallMs, double CpuMs, long WorkingSet, long WorkingSetDelta,
        long PeakWorkingSet, ulong ReadBytes, ulong WriteBytes, int PageFaults);

    private sealed record MeasuredAction(
        double WallMs, double CpuMs, double EffectiveCores, long WorkingSet, long WorkingSetDelta,
        long PeakWorkingSet, ulong ReadBytes, ulong WriteBytes, int PageFaults);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BuildDelegate(nint parquet, nint directory, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BuildShardDelegate(
        nint parquet, nint directory, nint operatorValue, int quarter, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateIndexesDelegate(nint directory, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenDelegate(nint directory, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryDelegate(
        nint engine, nint operatorValue, int quarter, long lowTimestamp, long highTimestamp,
        int explain, NativeMetrics* metrics);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(nint engine);
}
