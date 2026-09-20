using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Colossus.NumericFilterBench;

internal static unsafe class NativeDataFusion
{
    public static void Benchmark(string directory, string requested)
    {
        ProcessMetrics.PinToSixCores();
        ArtifactManifest manifest = ArtifactManifest.Read(directory);
        string libraryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "native", "target", "release",
            OperatingSystem.IsWindows() ? "colossus_datafusion_bench.dll" : "libcolossus_datafusion_bench.so"));
        nint library = NativeLibrary.Load(libraryPath);
        var open = Load<OpenDelegate>(library, "colossus_df_open");
        var query = Load<QueryDelegate>(library, "colossus_df_query");
        var close = Load<CloseDelegate>(library, "colossus_df_close");

        nint utf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint engine = 0;
        try
        {
            engine = open(utf8, Math.Min(Environment.ProcessorCount, 12));
            if (engine == 0)
                throw new InvalidOperationException("DataFusion failed to open the mapped Arrow artifact");
            string[] modes = requested == "both" ? ["scan", "driver"] : [requested];
            foreach (string mode in modes)
            {
                int nativeMode = mode switch
                {
                    "scan" => 0,
                    "driver" => 1,
                    _ => throw new ArgumentException($"Unknown DataFusion mode: {mode}")
                };
                foreach (RangeBox box in BenchmarkRunner.Cases(manifest))
                {
                    Invoke(query, engine, nativeMode, box);
                    NativeOutcome outcome = Invoke(query, engine, nativeMode, box);
                    if (!manifest.Expected.TryGetValue(box.Name, out ExpectedResult? expected))
                        throw new InvalidDataException($"No expected result for {box.Name}");
                    string exact = expected.Rows == (long)outcome.Metrics.Rows &&
                                   expected.Checksum == outcome.Metrics.Checksum
                        ? "bitExact=true"
                        : $"bitExact=false expectedRows={expected.Rows} expectedChecksum=0x{expected.Checksum:X16}";
                    Console.WriteLine(
                        $"engine=datafusion-{mode} query={box.Name} wallMs={outcome.WallMs:F2} " +
                        $"engineMs={outcome.Metrics.EngineMilliseconds:F2} cpuMs={outcome.CpuMs:F2} " +
                        $"rows={outcome.Metrics.Rows} checksum=0x{outcome.Metrics.Checksum:X16} " +
                        $"visited={outcome.Metrics.RecordsVisited} {exact} " +
                        $"workingSetMiB={outcome.WorkingSet / 1048576.0:F2} " +
                        $"workingSetDeltaMiB={outcome.WorkingSetDelta / 1048576.0:F2} " +
                        $"peakWorkingSetMiB={outcome.PeakWorkingSet / 1048576.0:F2}");
                }
            }
        }
        finally
        {
            if (engine != 0) close(engine);
            Marshal.FreeCoTaskMem(utf8);
            NativeLibrary.Free(library);
        }
    }

    public static void Pressure(string directory, string requested, int users)
    {
        ProcessMetrics.PinToSixCores();
        ArtifactManifest manifest = ArtifactManifest.Read(directory);
        int nativeMode = requested switch
        {
            "scan" => 0,
            "driver" => 1,
            _ => throw new ArgumentException($"Unknown DataFusion mode: {requested}")
        };
        string libraryPath = LibraryPath();
        nint library = NativeLibrary.Load(libraryPath);
        var open = Load<OpenDelegate>(library, "colossus_df_open");
        var query = Load<QueryDelegate>(library, "colossus_df_query");
        var close = Load<CloseDelegate>(library, "colossus_df_close");
        nint utf8 = Marshal.StringToCoTaskMemUTF8(directory);
        nint engine = 0;
        try
        {
            engine = open(utf8, Math.Min(Environment.ProcessorCount, 12));
            if (engine == 0)
                throw new InvalidOperationException("DataFusion failed to open the mapped Arrow artifact");
            RangeBox outer = RangeBox.Quantile(manifest, 25, 75);
            RangeBox inner = RangeBox.Quantile(manifest, 45, 55);
            InvokeRaw(query, engine, nativeMode, outer);

            var boxes = new RangeBox[users];
            for (int user = 0; user < users; user++)
            {
                float fraction = (user + 1f) / (users + 1f) * 0.10f;
                float[] low = new float[Fields.Names.Length];
                float[] high = new float[Fields.Names.Length];
                for (int d = 0; d < Fields.Names.Length; d++)
                {
                    low[d] = outer.Low[d] + (inner.Low[d] - outer.Low[d]) * fraction;
                    high[d] = outer.High[d] - (outer.High[d] - inner.High[d]) * fraction;
                }
                boxes[user] = new RangeBox(low, high, $"unique-middle-{user}");
            }

            var barrier = new Barrier(users + 1);
            var latency = new double[users];
            var rows = new ulong[users];
            var visited = new ulong[users];
            var threads = new Thread[users];
            for (int user = 0; user < users; user++)
            {
                int local = user;
                threads[user] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    RawOutcome outcome = InvokeRaw(query, engine, nativeMode, boxes[local]);
                    latency[local] = outcome.WallMs;
                    rows[local] = outcome.Metrics.Rows;
                    visited[local] = outcome.Metrics.RecordsVisited;
                }) { IsBackground = true, Name = $"datafusion-user-{user}" };
                threads[user].Start();
            }

            var process = Process.GetCurrentProcess();
            TimeSpan cpu0 = process.TotalProcessorTime;
            long ws0 = process.WorkingSet64;
            barrier.SignalAndWait();
            var burst = Stopwatch.StartNew();
            foreach (Thread thread in threads) thread.Join();
            burst.Stop();
            process.Refresh();
            Array.Sort(latency);
            Console.WriteLine(
                $"engine=datafusion-{requested}-pressure users={users} " +
                $"throughputQps={users / burst.Elapsed.TotalSeconds:F2} burstWallMs={burst.Elapsed.TotalMilliseconds:F2} " +
                $"p50Ms={Percentile(latency, .50):F2} p95Ms={Percentile(latency, .95):F2} " +
                $"p99Ms={Percentile(latency, .99):F2} rows={rows.Sum(x => (decimal)x)} " +
                $"visited={visited.Sum(x => (decimal)x)} " +
                $"cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} " +
                $"effectiveCores={(process.TotalProcessorTime - cpu0).TotalSeconds / burst.Elapsed.TotalSeconds:F2} " +
                $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
                $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
                $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");
        }
        finally
        {
            if (engine != 0) close(engine);
            Marshal.FreeCoTaskMem(utf8);
            NativeLibrary.Free(library);
        }
    }

    private static NativeOutcome Invoke(QueryDelegate query, nint engine, int mode, RangeBox box)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        NativeMetrics metrics = default;
        var wall = Stopwatch.StartNew();
        fixed (float* low = box.Low)
        fixed (float* high = box.High)
        {
            int status = query(engine, mode, low, high, &metrics);
            if (status != 0)
                throw new InvalidOperationException($"DataFusion query failed with native status {status}");
        }
        wall.Stop();
        process.Refresh();
        return new NativeOutcome(
            metrics, wall.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpu0).TotalMilliseconds,
            process.WorkingSet64, process.WorkingSet64 - ws0, process.PeakWorkingSet64);
    }

    private static RawOutcome InvokeRaw(QueryDelegate query, nint engine, int mode, RangeBox box)
    {
        NativeMetrics metrics = default;
        var wall = Stopwatch.StartNew();
        fixed (float* low = box.Low)
        fixed (float* high = box.High)
        {
            int status = query(engine, mode, low, high, &metrics);
            if (status != 0)
                throw new InvalidOperationException($"DataFusion query failed with native status {status}");
        }
        wall.Stop();
        return new RawOutcome(metrics, wall.Elapsed.TotalMilliseconds);
    }

    private static string LibraryPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "native", "target", "release",
        OperatingSystem.IsWindows() ? "colossus_datafusion_bench.dll" : "libcolossus_datafusion_bench.so"));

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        int index = Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static T Load<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMetrics
    {
        public double EngineMilliseconds;
        public ulong Rows;
        public ulong Checksum;
        public ulong RecordsVisited;
    }

    private sealed record NativeOutcome(
        NativeMetrics Metrics, double WallMs, double CpuMs,
        long WorkingSet, long WorkingSetDelta, long PeakWorkingSet);

    private sealed record RawOutcome(NativeMetrics Metrics, double WallMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OpenDelegate(nint directory, int threads);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryDelegate(nint engine, int mode, float* low, float* high, NativeMetrics* metrics);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(nint engine);
}
