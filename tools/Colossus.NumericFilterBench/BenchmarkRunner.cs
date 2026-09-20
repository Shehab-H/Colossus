using System.Diagnostics;
using System.IO.Compression;

namespace Colossus.NumericFilterBench;

internal static class BenchmarkRunner
{
    public static RangeBox[] Cases(ArtifactManifest manifest) =>
    [
        RangeBox.Quantile(manifest, 1, 99),
        RangeBox.Quantile(manifest, 5, 95),
        RangeBox.Quantile(manifest, 25, 75),
        RangeBox.Quantile(manifest, 45, 55)
    ];

    public static void Run(string directory, string requested)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new MappedArtifact(directory);
        var engine = new ExactQueryEngine(artifact);
        var workspace = new QueryWorkspace(artifact.Manifest.MarkCount);
        string[] engines = requested == "all"
            ? ["scan", "mark", "index", "bitmap", "cover", "index-delta", "bitmap-delta", "delta", "stateless-delta"]
            : [requested];

        Console.WriteLine($"facts={artifact.Manifest.FactCount:N0} marks={artifact.Manifest.MarkCount:N0} artifactGiB={artifact.Manifest.ArtifactBytes / 1073741824.0:F3}");
        foreach (string name in engines)
        {
            if (name is "delta" or "index-delta" or "bitmap-delta" or "small-delta")
            {
                RunDelta(artifact, engine, workspace, name);
                continue;
            }
            if (name == "stateless-delta")
            {
                RunStatelessDelta(artifact, engine);
                continue;
            }
            foreach (RangeBox box in Cases(artifact.Manifest))
            {
                QueryOutcome warm = Execute(name, engine, box, workspace);
                Validate(artifact.Manifest, warm);
                QueryOutcome measured = Execute(name, engine, box, workspace);
                Validate(artifact.Manifest, measured);
                Print(measured);
            }
        }
    }

    public static void RunOnce(string directory, string engineName, string queryName)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new MappedArtifact(directory);
        var engine = new ExactQueryEngine(artifact);
        var workspace = new QueryWorkspace(artifact.Manifest.MarkCount);
        RangeBox box = Cases(artifact.Manifest).Single(item => item.Name == queryName);
        QueryOutcome outcome = Execute(engineName, engine, box, workspace);
        Validate(artifact.Manifest, outcome);
        Print(outcome);
    }

    public static void Pressure(string directory, int users, int seconds, string mode)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new MappedArtifact(directory);
        var engine = new ExactQueryEngine(artifact);
        RangeBox q = RangeBox.Quantile(artifact.Manifest, 5, 95)
            .WithLower(Fields.Download, 10f, "download-10.00");
        RangeBox q2 = q.WithLower(Fields.Download, 10.25f, "download-10.25");

        if (mode is "stateless-varint" or "stateless-index-varint")
        {
            PressureStateless(
                artifact, engine, q, q2, users, seconds,
                coveringBoundary: mode == "stateless-varint");
            return;
        }

        var template = new QueryWorkspace(artifact.Manifest.MarkCount);
        engine.ScanState(q, template);
        var states = new QueryWorkspace[users];
        for (int i = 0; i < users; i++) states[i] = template.CloneDirect();

        var start = new Barrier(users + 1);
        using var stop = new CancellationTokenSource();
        var samples = new List<double>[users];
        var computeSamples = new List<double>[users];
        var encodeSamples = new List<double>[users];
        var compressSamples = new List<double>[users];
        var userRequests = new long[users];
        var userChangedMarks = new long[users];
        var userOutputBytes = new long[users];
        var threads = new Thread[users];
        for (int user = 0; user < users; user++)
        {
            int localUser = user;
            threads[user] = new Thread(() =>
            {
                var local = new List<double>(4096);
                var localCompute = new List<double>(4096);
                var localEncode = new List<double>(4096);
                var localCompress = new List<double>(4096);
                samples[localUser] = local;
                computeSamples[localUser] = localCompute;
                encodeSamples[localUser] = localEncode;
                compressSamples[localUser] = localCompress;
                bool atSecond = false;
                start.SignalAndWait();
                while (!stop.IsCancellationRequested)
                {
                    long requestStarted = Stopwatch.GetTimestamp();
                    string computeMode = mode.StartsWith("cover-", StringComparison.Ordinal) ? "cover" : mode;
                    var result = computeMode switch
                    {
                        "cover" when atSecond => engine.DeltaFast(q2, q, Fields.Download, states[localUser]),
                        "cover" => engine.DeltaFast(q, q2, Fields.Download, states[localUser]),
                        "bitmap" when atSecond => engine.BitmapDeltaFast(q2, q, Fields.Download, states[localUser]),
                        "bitmap" => engine.BitmapDeltaFast(q, q2, Fields.Download, states[localUser]),
                        "index" when atSecond => engine.DeltaFast(q2, q, Fields.Download, states[localUser], covering: false),
                        "index" => engine.DeltaFast(q, q2, Fields.Download, states[localUser], covering: false),
                        _ => throw new ArgumentException($"Unknown delta pressure mode: {mode}")
                    };
                    double encodeMs = 0;
                    double compressMs = 0;
                    if (mode is "cover-encode" or "cover-brotli" or "cover-varint" or "cover-varint-brotli")
                    {
                        long encodeStarted = Stopwatch.GetTimestamp();
                        bool varint = mode.StartsWith("cover-varint", StringComparison.Ordinal);
                        byte[] payload;
                        int payloadLength;
                        if (varint)
                        {
                            (payload, payloadLength) = states[localUser].EncodeChangedVarint((uint)userRequests[localUser]);
                        }
                        else
                        {
                            payload = states[localUser].EncodeChanged((uint)userRequests[localUser]);
                            payloadLength = payload.Length;
                        }
                        encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
                        if (mode is "cover-brotli" or "cover-varint-brotli")
                        {
                            long compressStarted = Stopwatch.GetTimestamp();
                            byte[] compressed = GC.AllocateUninitializedArray<byte>(BrotliEncoder.GetMaxCompressedLength(payloadLength));
                            if (!BrotliEncoder.TryCompress(payload.AsSpan(0, payloadLength), compressed, out int written, quality: 1, window: 22))
                                throw new InvalidOperationException("Brotli compression failed");
                            compressMs = Stopwatch.GetElapsedTime(compressStarted).TotalMilliseconds;
                            userOutputBytes[localUser] += written;
                            GC.KeepAlive(compressed);
                        }
                        else
                        {
                            userOutputBytes[localUser] += payloadLength;
                        }
                        GC.KeepAlive(payload);
                    }
                    atSecond = !atSecond;
                    local.Add(Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
                    localCompute.Add(result.Milliseconds);
                    localEncode.Add(encodeMs);
                    localCompress.Add(compressMs);
                    userRequests[localUser]++;
                    userChangedMarks[localUser] += result.Changed.Rows;
                }
            }) { IsBackground = true, Name = $"numeric-user-{user}" };
            threads[user].Start();
        }

        var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        int faults0 = ProcessMetrics.PageFaults();
        start.SignalAndWait();
        var wall = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        stop.Cancel();
        foreach (Thread thread in threads) thread.Join();
        wall.Stop();
        process.Refresh();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        int faults1 = ProcessMetrics.PageFaults();

        double[] latency = samples.SelectMany(x => x).Order().ToArray();
        double[] computeLatency = computeSamples.SelectMany(x => x).Order().ToArray();
        double[] encodeLatency = encodeSamples.SelectMany(x => x).Order().ToArray();
        double[] compressLatency = compressSamples.SelectMany(x => x).Order().ToArray();
        long requests = userRequests.Sum();
        long changedMarks = userChangedMarks.Sum();
        long outputBytes = userOutputBytes.Sum();
        Console.WriteLine(
            $"engine={mode}-delta-pressure users={users} seconds={wall.Elapsed.TotalSeconds:F3} " +
            $"requests={requests} throughputQps={requests / wall.Elapsed.TotalSeconds:F2} " +
            $"avgChangedMarks={(requests == 0 ? 0 : (double)changedMarks / requests):F2} " +
            $"avgOutputKiB={(requests == 0 ? 0 : outputBytes / 1024.0 / requests):F2} " +
            $"p50Ms={Percentile(latency, .50):F2} p95Ms={Percentile(latency, .95):F2} " +
            $"p99Ms={Percentile(latency, .99):F2} maxMs={(latency.Length == 0 ? 0 : latency[^1]):F2} " +
            $"computeP50Ms={Percentile(computeLatency, .50):F2} computeP95Ms={Percentile(computeLatency, .95):F2} " +
            $"encodeP50Ms={Percentile(encodeLatency, .50):F2} encodeP95Ms={Percentile(encodeLatency, .95):F2} " +
            $"compressP50Ms={Percentile(compressLatency, .50):F2} compressP95Ms={Percentile(compressLatency, .95):F2} " +
            $"cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} " +
            $"effectiveCores={(process.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds:F2} " +
            $"pageFaults={unchecked(faults1 - faults0)} " +
            $"processReadMiB={(read1 - read0) / 1048576.0:F2} processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");

        for (int i = 0; i < users; i++)
        {
            ExpectedResult actual = states[i].Complete(artifact);
            RangeBox expectedBox = userRequests[i] % 2 == 0 ? q : q2;
            if (artifact.Manifest.Expected.TryGetValue(expectedBox.Name, out ExpectedResult? expected) &&
                actual != expected)
                throw new InvalidDataException($"Pressure state mismatch for user {i}");
        }
    }

    public static void InitialPressure(string directory, int users, string family, string mode)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new MappedArtifact(directory);
        var engine = new ExactQueryEngine(artifact);
        RangeBox baseBox = family switch
        {
            "broad" => RangeBox.Quantile(artifact.Manifest, 5, 95),
            "middle" => RangeBox.Quantile(artifact.Manifest, 25, 75),
            _ => throw new ArgumentException($"Unknown initial-query family: {family}")
        };
        RangeBox innerBox = family switch
        {
            "broad" => RangeBox.Quantile(artifact.Manifest, 10, 90),
            "middle" => RangeBox.Quantile(artifact.Manifest, 45, 55),
            _ => throw new UnreachableException()
        };
        var warmWorkspace = new QueryWorkspace(artifact.Manifest.MarkCount);
        if (mode == "cover") engine.CoveringFast(baseBox, warmWorkspace);
        else if (mode == "bitmap") engine.BitmapFast(baseBox, warmWorkspace);
        else throw new ArgumentException($"Unknown initial-query mode: {mode}");

        var boxes = new RangeBox[users];
        var workspaces = new QueryWorkspace[users];
        for (int user = 0; user < users; user++)
        {
            float fraction = (user + 1f) / (users + 1f) * 0.20f;
            float[] low = new float[Fields.Names.Length];
            float[] high = new float[Fields.Names.Length];
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                low[d] = baseBox.Low[d] + (innerBox.Low[d] - baseBox.Low[d]) * fraction;
                high[d] = baseBox.High[d] - (baseBox.High[d] - innerBox.High[d]) * fraction;
            }
            boxes[user] = new RangeBox(low, high, $"unique-broad-{user}");
            workspaces[user] = new QueryWorkspace(artifact.Manifest.MarkCount);
        }

        var barrier = new Barrier(users + 1);
        var latency = new double[users];
        var visited = new long[users];
        var bytes = new long[users];
        var threads = new Thread[users];
        for (int user = 0; user < users; user++)
        {
            int local = user;
            threads[user] = new Thread(() =>
            {
                barrier.SignalAndWait();
                var result = mode == "cover"
                    ? engine.CoveringFast(boxes[local], workspaces[local])
                    : engine.BitmapFast(boxes[local], workspaces[local]);
                latency[local] = result.Milliseconds;
                visited[local] = result.Visited;
                bytes[local] = result.LogicalBytes;
            }) { IsBackground = true, Name = $"initial-user-{user}" };
            threads[user].Start();
        }

        var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        barrier.SignalAndWait();
        var wall = Stopwatch.StartNew();
        foreach (Thread thread in threads) thread.Join();
        wall.Stop();
        process.Refresh();
        Array.Sort(latency);
        Console.WriteLine(
            $"engine={mode}-initial-pressure family={family} users={users} throughputQps={users / wall.Elapsed.TotalSeconds:F2} " +
            $"burstWallMs={wall.Elapsed.TotalMilliseconds:F2} p50Ms={Percentile(latency, .50):F2} " +
            $"p95Ms={Percentile(latency, .95):F2} p99Ms={Percentile(latency, .99):F2} " +
            $"cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} " +
            $"effectiveCores={(process.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds:F2} " +
            $"logicalGiB={bytes.Sum() / 1073741824.0:F3} visited={visited.Sum()} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static void RunDelta(
        MappedArtifact artifact, ExactQueryEngine engine, QueryWorkspace workspace, string mode)
    {
        RangeBox oldBox = RangeBox.Quantile(artifact.Manifest, 5, 95);
        RangeBox newBox = oldBox.WithLower(Fields.Download, 10f, "download-10.00");
        if (mode == "small-delta")
        {
            oldBox = newBox;
            newBox = oldBox.WithLower(Fields.Download, 10.25f, "download-10.25");
        }
        engine.ScanState(oldBox, workspace);
        QueryOutcome warm = ExecuteDelta(oldBox, newBox);
        ValidateFull(artifact, workspace, newBox.Name);
        ExecuteDelta(newBox, oldBox);
        ValidateFull(artifact, workspace, oldBox.Name);
        QueryOutcome measured = ExecuteDelta(oldBox, newBox);
        ValidateFull(artifact, workspace, newBox.Name);
        Print(measured);

        QueryOutcome ExecuteDelta(RangeBox from, RangeBox to) => mode switch
        {
            "delta" => engine.Delta(from, to, Fields.Download, workspace, covering: true),
            "small-delta" => engine.Delta(from, to, Fields.Download, workspace, covering: true),
            "index-delta" => engine.Delta(from, to, Fields.Download, workspace, covering: false),
            "bitmap-delta" => engine.BitmapDelta(from, to, Fields.Download, workspace),
            _ => throw new ArgumentException($"Unknown delta mode: {mode}")
        };
    }

    private static void RunStatelessDelta(MappedArtifact artifact, ExactQueryEngine engine)
    {
        RangeBox baseBox = RangeBox.Quantile(artifact.Manifest, 5, 95);
        RangeBox q = baseBox
            .WithLower(Fields.Download, 10f, "download-10.00");
        RangeBox q2 = q.WithLower(Fields.Download, 10.25f, "download-10.25");

        var dense = new QueryWorkspace(artifact.Manifest.MarkCount);
        var workspace = new StatelessWorkspace(artifact.Manifest.MarkCount);
        engine.ScanState(baseBox, dense);
        var expectedLarge = engine.DeltaFast(baseBox, q, Fields.Download, dense).Changed;
        engine.StatelessDeltaFast(baseBox, q, Fields.Download, workspace, 0);
        StatelessDeltaOutcome large = engine.StatelessDeltaFast(baseBox, q, Fields.Download, workspace, 1);
        if (large.Changed != expectedLarge)
            throw new InvalidDataException("Stateless large delta differs from exact stateful reference");
        PrintStateless(large, q.Name, "stateless-cover-delta");
        StatelessDeltaOutcome largeCompact = engine.StatelessDeltaFast(
            baseBox, q, Fields.Download, workspace, 1, coveringBoundary: false);
        if (largeCompact.Changed != expectedLarge)
            throw new InvalidDataException("Stateless compact-index large delta differs from exact reference");
        PrintStateless(largeCompact, q.Name, "stateless-index-delta");

        engine.ScanState(q, dense);
        var expectedForward = engine.DeltaFast(q, q2, Fields.Download, dense).Changed;
        StatelessDeltaOutcome warm = engine.StatelessDeltaFast(q, q2, Fields.Download, workspace, 2);
        if (warm.Changed != expectedForward)
            throw new InvalidDataException("Stateless forward delta differs from exact stateful reference");

        var expectedReverse = engine.DeltaFast(q2, q, Fields.Download, dense).Changed;
        StatelessDeltaOutcome reverse = engine.StatelessDeltaFast(q2, q, Fields.Download, workspace, 3);
        if (reverse.Changed != expectedReverse)
            throw new InvalidDataException("Stateless reverse delta differs from exact stateful reference");

        StatelessDeltaOutcome measured = engine.StatelessDeltaFast(q, q2, Fields.Download, workspace, 4);
        if (measured.Changed != expectedForward)
            throw new InvalidDataException("Stateless measured delta differs from exact reference");
        PrintStateless(measured, q2.Name, "stateless-cover-delta");
        StatelessDeltaOutcome compact = engine.StatelessDeltaFast(
            q, q2, Fields.Download, workspace, 4, coveringBoundary: false);
        if (compact.Changed != expectedForward)
            throw new InvalidDataException("Stateless compact-index small delta differs from exact reference");
        PrintStateless(compact, q2.Name, "stateless-index-delta");

        static void PrintStateless(StatelessDeltaOutcome result, string query, string engine) => Console.WriteLine(
            $"engine={engine} query={query} totalMs={result.TotalMilliseconds:F2} " +
            $"boundaryMs={result.BoundaryMilliseconds:F2} recomputeMs={result.RecomputeMilliseconds:F2} " +
            $"encodeMs={result.EncodeMilliseconds:F2} changedMarks={result.Changed.Rows} " +
            $"checksum=0x{result.Changed.Checksum:X16} boundaryFacts={result.BoundaryFacts} " +
            $"recomputedFacts={result.RecomputedFacts} logicalMiB={result.LogicalBytes / 1048576.0:F2} " +
            $"outputKiB={result.Length / 1024.0:F2} " +
            $"workingSetMiB={Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F2}");
    }

    private static void PressureStateless(
        MappedArtifact artifact, ExactQueryEngine engine,
        RangeBox q, RangeBox q2, int users, int seconds, bool coveringBoundary)
    {
        var workspaces = new StatelessWorkspace[users];
        for (int i = 0; i < users; i++)
        {
            workspaces[i] = new StatelessWorkspace(artifact.Manifest.MarkCount);
            engine.StatelessDeltaFast(
                q, q2, Fields.Download, workspaces[i], 0, coveringBoundary);
        }

        var start = new Barrier(users + 1);
        using var stop = new CancellationTokenSource();
        var totalSamples = new List<double>[users];
        var boundarySamples = new List<double>[users];
        var recomputeSamples = new List<double>[users];
        var encodeSamples = new List<double>[users];
        var requests = new long[users];
        var changedMarks = new long[users];
        var boundaryFacts = new long[users];
        var recomputedFacts = new long[users];
        var logicalBytes = new long[users];
        var outputBytes = new long[users];
        var threads = new Thread[users];
        for (int user = 0; user < users; user++)
        {
            int localUser = user;
            threads[user] = new Thread(() =>
            {
                var localTotal = new List<double>(4096);
                var localBoundary = new List<double>(4096);
                var localRecompute = new List<double>(4096);
                var localEncode = new List<double>(4096);
                totalSamples[localUser] = localTotal;
                boundarySamples[localUser] = localBoundary;
                recomputeSamples[localUser] = localRecompute;
                encodeSamples[localUser] = localEncode;
                bool atSecond = false;
                start.SignalAndWait();
                while (!stop.IsCancellationRequested)
                {
                    StatelessDeltaOutcome result = atSecond
                        ? engine.StatelessDeltaFast(q2, q, Fields.Download, workspaces[localUser], (uint)requests[localUser], coveringBoundary)
                        : engine.StatelessDeltaFast(q, q2, Fields.Download, workspaces[localUser], (uint)requests[localUser], coveringBoundary);
                    atSecond = !atSecond;
                    localTotal.Add(result.TotalMilliseconds);
                    localBoundary.Add(result.BoundaryMilliseconds);
                    localRecompute.Add(result.RecomputeMilliseconds);
                    localEncode.Add(result.EncodeMilliseconds);
                    requests[localUser]++;
                    changedMarks[localUser] += result.Changed.Rows;
                    boundaryFacts[localUser] += result.BoundaryFacts;
                    recomputedFacts[localUser] += result.RecomputedFacts;
                    logicalBytes[localUser] += result.LogicalBytes;
                    outputBytes[localUser] += result.Length;
                }
            }) { IsBackground = true, Name = $"stateless-user-{user}" };
            threads[user].Start();
        }

        var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        int faults0 = ProcessMetrics.PageFaults();
        start.SignalAndWait();
        var wall = Stopwatch.StartNew();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        stop.Cancel();
        foreach (Thread thread in threads) thread.Join();
        wall.Stop();
        process.Refresh();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        int faults1 = ProcessMetrics.PageFaults();

        double[] total = totalSamples.SelectMany(x => x).Order().ToArray();
        double[] boundary = boundarySamples.SelectMany(x => x).Order().ToArray();
        double[] recompute = recomputeSamples.SelectMany(x => x).Order().ToArray();
        double[] encode = encodeSamples.SelectMany(x => x).Order().ToArray();
        long requestCount = requests.Sum();
        Console.WriteLine(
            $"engine={(coveringBoundary ? "stateless-cover" : "stateless-index")}-varint-delta-pressure users={users} seconds={wall.Elapsed.TotalSeconds:F3} " +
            $"requests={requestCount} throughputQps={requestCount / wall.Elapsed.TotalSeconds:F2} " +
            $"avgChangedMarks={(requestCount == 0 ? 0 : (double)changedMarks.Sum() / requestCount):F2} " +
            $"avgBoundaryFacts={(requestCount == 0 ? 0 : (double)boundaryFacts.Sum() / requestCount):F2} " +
            $"avgRecomputedFacts={(requestCount == 0 ? 0 : (double)recomputedFacts.Sum() / requestCount):F2} " +
            $"avgLogicalMiB={(requestCount == 0 ? 0 : logicalBytes.Sum() / 1048576.0 / requestCount):F2} " +
            $"avgOutputKiB={(requestCount == 0 ? 0 : outputBytes.Sum() / 1024.0 / requestCount):F2} " +
            $"p50Ms={Percentile(total, .50):F2} p95Ms={Percentile(total, .95):F2} " +
            $"p99Ms={Percentile(total, .99):F2} maxMs={(total.Length == 0 ? 0 : total[^1]):F2} " +
            $"boundaryP50Ms={Percentile(boundary, .50):F2} boundaryP95Ms={Percentile(boundary, .95):F2} " +
            $"recomputeP50Ms={Percentile(recompute, .50):F2} recomputeP95Ms={Percentile(recompute, .95):F2} " +
            $"encodeP50Ms={Percentile(encode, .50):F2} encodeP95Ms={Percentile(encode, .95):F2} " +
            $"cpuMs={(process.TotalProcessorTime - cpu0).TotalMilliseconds:F2} " +
            $"effectiveCores={(process.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds:F2} " +
            $"pageFaults={unchecked(faults1 - faults0)} " +
            $"processReadMiB={(read1 - read0) / 1048576.0:F2} processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static QueryOutcome Execute(
        string name, ExactQueryEngine engine, RangeBox box, QueryWorkspace workspace) => name switch
    {
        "scan" => engine.Scan(box, workspace),
        "mark" => engine.MarkPruned(box, workspace),
        "index" => engine.SharedIndex(box, workspace),
        "bitmap" => engine.BitmapIndex(box, workspace),
        "cover" => engine.CoveringIndex(box, workspace),
        _ => throw new ArgumentException($"Unknown engine: {name}")
    };

    private static void Validate(ArtifactManifest manifest, QueryOutcome outcome)
    {
        if (!manifest.Expected.TryGetValue(outcome.Query, out ExpectedResult? expected))
            throw new InvalidDataException($"No expected result for {outcome.Query}");
        if (expected.Rows != outcome.Rows || expected.Checksum != outcome.Checksum)
            throw new InvalidDataException(
                $"Exactness failure: {outcome.Engine}/{outcome.Query}; " +
                $"expected rows={expected.Rows} checksum=0x{expected.Checksum:X16}, " +
                $"actual rows={outcome.Rows} checksum=0x{outcome.Checksum:X16}");
    }

    private static void ValidateFull(MappedArtifact artifact, QueryWorkspace workspace, string name)
    {
        ExpectedResult actual = workspace.Complete(artifact);
        if (!artifact.Manifest.Expected.TryGetValue(name, out ExpectedResult? expected) || expected != actual)
            throw new InvalidDataException($"Exactness failure after delta: {name}");
    }

    private static void Print(QueryOutcome result) => Console.WriteLine(
        $"engine={result.Engine} query={result.Query} wallMs={result.WallMilliseconds:F2} " +
        $"cpuMs={result.CpuMilliseconds:F2} rows={result.Rows} checksum=0x{result.Checksum:X16} " +
        $"visited={result.RecordsVisited} unique={result.UniqueFacts} " +
        $"logicalMiB={result.LogicalBytes / 1048576.0:F2} pageFaults={result.PageFaults} " +
        $"processReadMiB={result.ProcessReadBytes / 1048576.0:F2} processWriteMiB={result.ProcessWriteBytes / 1048576.0:F2} " +
        $"workingSetMiB={result.WorkingSetBytes / 1048576.0:F2} " +
        $"workingSetDeltaMiB={result.WorkingSetDeltaBytes / 1048576.0:F2} " +
        $"peakWorkingSetMiB={result.PeakWorkingSetBytes / 1048576.0:F2}");

    private static double Percentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        int index = Math.Clamp((int)Math.Ceiling(values.Length * p) - 1, 0, values.Length - 1);
        return values[index];
    }
}
