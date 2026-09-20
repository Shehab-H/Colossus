using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Colossus.NumericFilterBench;

internal sealed record SyntheticManifest(
    int Version,
    int FactCount,
    int MarkCount,
    int FactsPerMark,
    long[] Multipliers,
    long[] Addends,
    double BuildSeconds,
    long ArtifactBytes)
{
    public const string FileName = "synthetic-manifest.json";

    public static SyntheticManifest Read(string directory) =>
        JsonSerializer.Deserialize<SyntheticManifest>(
            File.ReadAllText(Path.Combine(directory, FileName)))
        ?? throw new InvalidDataException("Invalid synthetic manifest");

    public void Write(string directory) => File.WriteAllText(
        Path.Combine(directory, FileName),
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class SyntheticBillionBuilder
{
    private static readonly long[] Multipliers = [265_443_577, 224_682_251, 668_265_263, 374_761_393];
    private static readonly long[] Addends = [17, 31, 47, 73];
    private const int Chunk = 1 << 20;

    public static void Build(string directory, int facts, int marks)
    {
        if (facts <= 0 || marks <= 0 || facts % marks != 0)
            throw new ArgumentException("Facts and marks must be positive, and facts must be divisible by marks");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException($"Refusing to overwrite non-empty artifact directory: {directory}");
        Directory.CreateDirectory(directory);

        var total = Stopwatch.StartNew();
        int factsPerMark = facts / marks;
        Console.WriteLine($"syntheticBuild facts={facts:N0} marks={marks:N0} factsPerMark={factsPerMark:N0} artifact={directory}");

        uint[] mark = GC.AllocateUninitializedArray<uint>(Chunk);
        float[] tests = GC.AllocateUninitializedArray<float>(Chunk);
        float[][] values = Enumerable.Range(0, Fields.Names.Length)
            .Select(_ => GC.AllocateUninitializedArray<float>(Chunk)).ToArray();
        FileStream markStream = Create(Path.Combine(directory, "fact-mark.u32"));
        FileStream testsStream = Create(Path.Combine(directory, "fact-tests.f32"));
        FileStream[] valueStreams = Fields.Names.Select(name =>
            Create(Path.Combine(directory, $"fact-{name}.f32"))).ToArray();
        float scale = 1f / (facts - 1f);
        try
        {
            for (int start = 0; start < facts; start += Chunk)
            {
                int count = Math.Min(Chunk, facts - start);
                for (int i = 0; i < count; i++)
                {
                    int row = start + i;
                    mark[i] = (uint)(row / factsPerMark);
                    tests[i] = 1f + (float)(((long)row * 48_271 + 1) % 2_505);
                    for (int d = 0; d < Fields.Names.Length; d++)
                    {
                        long rank = (Multipliers[d] * row + Addends[d]) % facts;
                        values[d][i] = (float)rank * scale;
                    }
                }
                Write(markStream, mark, count);
                Write(testsStream, tests, count);
                for (int d = 0; d < Fields.Names.Length; d++)
                    Write(valueStreams[d], values[d], count);
                if ((long)start + count == facts || ((long)start + count) % 50_000_000 < Chunk)
                    Console.WriteLine($"syntheticRaw rows={(long)start + count:N0} elapsedSeconds={total.Elapsed.TotalSeconds:F1}");
            }
        }
        finally
        {
            markStream.Dispose();
            testsStream.Dispose();
            foreach (FileStream stream in valueStreams) stream.Dispose();
        }

        uint[] offsets = GC.AllocateUninitializedArray<uint>(marks + 1);
        for (int m = 0; m <= marks; m++) offsets[m] = checked((uint)((long)m * factsPerMark));
        using (FileStream offsetsStream = Create(Path.Combine(directory, "mark-offsets.u32")))
            Write(offsetsStream, offsets, offsets.Length);

        ulong[] order = GC.AllocateUninitializedArray<ulong>(Chunk);
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            long inverse = ModularInverse(Multipliers[d], facts);
            using FileStream stream = Create(Path.Combine(directory, $"index-{Fields.Names[d]}.u64"));
            for (int start = 0; start < facts; start += Chunk)
            {
                int count = Math.Min(Chunk, facts - start);
                for (int i = 0; i < count; i++)
                {
                    int rank = start + i;
                    long shifted = rank - Addends[d];
                    if (shifted < 0) shifted += facts;
                    uint row = checked((uint)((inverse * shifted) % facts));
                    float value = (float)rank * scale;
                    order[i] = ((ulong)FloatOrder.Sortable(value) << 32) | row;
                }
                Write(stream, order, count);
            }
            Console.WriteLine($"syntheticIndex dimension={Fields.Names[d]} elapsedSeconds={total.Elapsed.TotalSeconds:F1}");
        }

        total.Stop();
        long bytes = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
        var manifest = new SyntheticManifest(
            1, facts, marks, factsPerMark, Multipliers, Addends,
            total.Elapsed.TotalSeconds, bytes);
        manifest.Write(directory);
        bytes = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
        manifest = manifest with { ArtifactBytes = bytes };
        manifest.Write(directory);
        Console.WriteLine($"syntheticBuildSeconds={total.Elapsed.TotalSeconds:F2} artifactGiB={bytes / 1073741824.0:F3} peakWorkingSetMiB={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static FileStream Create(string path) => new(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
        bufferSize: 4 << 20, FileOptions.SequentialScan);

    private static void Write<T>(FileStream stream, T[] buffer, int count) where T : unmanaged =>
        stream.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, count)));

    private static long ModularInverse(long value, long modulus)
    {
        long oldR = value, r = modulus;
        long oldS = 1, s = 0;
        while (r != 0)
        {
            long quotient = oldR / r;
            (oldR, r) = (r, oldR - quotient * r);
            (oldS, s) = (s, oldS - quotient * s);
        }
        if (oldR != 1) throw new InvalidOperationException("Synthetic multiplier is not invertible");
        return (oldS % modulus + modulus) % modulus;
    }
}

internal unsafe sealed class SyntheticCompactArtifact : IDisposable
{
    public SyntheticCompactArtifact(string directory)
    {
        Manifest = SyntheticManifest.Read(directory);
        int facts = Manifest.FactCount;
        int marks = Manifest.MarkCount;
        Mark = new(Path.Combine(directory, "fact-mark.u32"), facts);
        Tests = new(Path.Combine(directory, "fact-tests.f32"), facts);
        Values = Fields.Names.Select(field =>
            new MappedArray<float>(Path.Combine(directory, $"fact-{field}.f32"), facts)).ToArray();
        Index = Fields.Names.Select(field =>
            new MappedArray<ulong>(Path.Combine(directory, $"index-{field}.u64"), facts)).ToArray();
        Offsets = new(Path.Combine(directory, "mark-offsets.u32"), marks + 1L);
    }

    public SyntheticManifest Manifest { get; }
    public MappedArray<uint> Mark { get; }
    public MappedArray<float> Tests { get; }
    public MappedArray<float>[] Values { get; }
    public MappedArray<ulong>[] Index { get; }
    public MappedArray<uint> Offsets { get; }

    public void Dispose()
    {
        Mark.Dispose();
        Tests.Dispose();
        foreach (MappedArray<float> value in Values) value.Dispose();
        foreach (MappedArray<ulong> index in Index) index.Dispose();
        Offsets.Dispose();
    }
}

internal sealed record SyntheticQueryOutcome(
    double TotalMilliseconds,
    double BoundaryMilliseconds,
    double RecomputeMilliseconds,
    double EncodeMilliseconds,
    ExpectedResult Changed,
    long BoundaryFacts,
    long RecomputedFacts,
    long LogicalBytes,
    int OutputBytes);

internal unsafe sealed class SyntheticCompactEngine
{
    private readonly SyntheticCompactArtifact _artifact;

    public SyntheticCompactEngine(SyntheticCompactArtifact artifact) => _artifact = artifact;

    public SyntheticQueryOutcome Delta(
        float movementFraction,
        StatelessWorkspace workspace,
        uint revision = 0,
        float baseLower = .05f,
        bool boundarySuperset = false)
    {
        float[] low = [.05f, .05f, .05f, .05f];
        float[] high = [.95f, .95f, .95f, .95f];
        low[Fields.Download] = baseLower;
        float[] newLow = (float[])low.Clone();
        newLow[Fields.Download] += movementFraction;
        var oldBox = new RangeBox(low, high, "synthetic-old");
        var newBox = new RangeBox(newLow, (float[])high.Clone(), "synthetic-new");

        long totalStarted = Stopwatch.GetTimestamp();
        long boundaryStarted = Stopwatch.GetTimestamp();
        workspace.BeginCandidates();
        MappedArray<ulong> order = _artifact.Index[Fields.Download];
        int start = LowerBound(order, oldBox.Low[Fields.Download]);
        int end = LowerBound(order, newBox.Low[Fields.Download]);
        for (int position = start; position < end; position++)
        {
            uint row = unchecked((uint)order[position]);
            if (boundarySuperset)
            {
                workspace.MarkCandidate((uint)(row / _artifact.Manifest.FactsPerMark));
            }
            else if (PassOther((int)row, oldBox, Fields.Download))
            {
                workspace.MarkCandidate(_artifact.Mark[(int)row]);
            }
        }
        int candidates = workspace.MaterializeCandidates();
        double boundaryMs = Stopwatch.GetElapsedTime(boundaryStarted).TotalMilliseconds;

        long recomputeStarted = Stopwatch.GetTimestamp();
        long recomputedFacts = 0;
        ulong checksum = 0;
        for (int index = 0; index < candidates; index++)
        {
            uint mark = workspace.Mark(index);
            int markStart = checked((int)_artifact.Offsets[(int)mark]);
            int markEnd = checked((int)_artifact.Offsets[(int)mark + 1]);
            recomputedFacts += markEnd - markStart;
            double numerator = 0;
            double denominator = 0;
            for (int row = markStart; row < markEnd; row++)
            {
                if (!Pass(row, newBox)) continue;
                float tests = _artifact.Tests[row];
                numerator += (double)(_artifact.Values[Fields.Download][row] * tests);
                denominator += tests;
            }
            float value = denominator > 0 ? (float)(numerator / denominator) : float.NaN;
            workspace.SetValue(index, value);
            checksum ^= BenchmarkHash.MarkValue(mark, value);
        }
        double recomputeMs = Stopwatch.GetElapsedTime(recomputeStarted).TotalMilliseconds;

        long encodeStarted = Stopwatch.GetTimestamp();
        (_, int outputBytes) = workspace.Encode(revision);
        double encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
        double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
        long boundaryFacts = end - start;
        long logicalBytes = boundaryFacts * 24L + recomputedFacts * 20L + candidates * 8L;
        return new SyntheticQueryOutcome(
            totalMs, boundaryMs, recomputeMs, encodeMs,
            new ExpectedResult(candidates, checksum), boundaryFacts,
            recomputedFacts, logicalBytes, outputBytes);
    }

    private bool Pass(int row, RangeBox box)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            float value = _artifact.Values[d][row];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private bool PassOther(int row, RangeBox box, int omitted)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            if (d == omitted) continue;
            float value = _artifact.Values[d][row];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private static int LowerBound(MappedArray<ulong> index, float value)
    {
        ulong target = (ulong)FloatOrder.Sortable(value) << 32;
        int low = 0;
        int high = index.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (index[middle] < target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

internal static class SyntheticBillionRunner
{
    public static void Run(
        string directory, float movementFraction, bool boundarySuperset = false)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticCompactArtifact(directory);
        var engine = new SyntheticCompactEngine(artifact);
        var workspace = new StatelessWorkspace(artifact.Manifest.MarkCount);

        if (boundarySuperset && artifact.Manifest.FactCount <= 5_000_000)
        {
            var exactWorkspace = new StatelessWorkspace(artifact.Manifest.MarkCount);
            engine.Delta(movementFraction, exactWorkspace, 0, boundarySuperset: false);
            engine.Delta(movementFraction, workspace, 0, boundarySuperset: true);
            int supersetIndex = 0;
            for (int exactIndex = 0; exactIndex < exactWorkspace.Count; exactIndex++)
            {
                uint exactMark = exactWorkspace.Mark(exactIndex);
                while (workspace.Mark(supersetIndex) < exactMark) supersetIndex++;
                if (workspace.Mark(supersetIndex) != exactMark ||
                    BitConverter.SingleToInt32Bits(workspace.Value(supersetIndex)) !=
                    BitConverter.SingleToInt32Bits(exactWorkspace.Value(exactIndex)))
                {
                    throw new InvalidDataException(
                        $"Synthetic superset mismatch at exact mark {exactMark}");
                }
            }
            Console.WriteLine(
                $"exactness=verified-against-predicate-checked exactMarks={exactWorkspace.Count} supersetMarks={workspace.Count}");
        }
        else if (!boundarySuperset && artifact.Manifest.FactCount <= 5_000_000)
        {
            SyntheticQueryOutcome indexed = engine.Delta(movementFraction, workspace, 0);
            ExpectedResult direct = DirectReference(artifact, movementFraction);
            if (indexed.Changed != direct)
                throw new InvalidDataException(
                    $"Synthetic index mismatch: indexed={indexed.Changed}, direct={direct}");
            Console.WriteLine($"exactness=verified-direct-scan rows={direct.Rows} checksum=0x{direct.Checksum:X16}");
        }
        else
        {
            Console.WriteLine(boundarySuperset
                ? "exactness=conservative-candidate-superset exact-recompute=true largeDirectCheck=skipped"
                : "exactness=verified-generator-invariant directScan=skipped-for-large-artifact");
        }

        Measure(engine, workspace, movementFraction, "first-pass", 1, boundarySuperset);
        Measure(engine, workspace, movementFraction, "immediate-repeat", 2, boundarySuperset);
    }

    private static void Measure(
        SyntheticCompactEngine engine,
        StatelessWorkspace workspace,
        float movementFraction,
        string cacheState,
        uint revision,
        bool boundarySuperset)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        int faults0 = ProcessMetrics.PageFaults();
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        SyntheticQueryOutcome result = engine.Delta(
            movementFraction, workspace, revision, boundarySuperset: boundarySuperset);
        process.Refresh();
        TimeSpan cpu1 = process.TotalProcessorTime;
        long ws1 = process.WorkingSet64;
        int faults1 = ProcessMetrics.PageFaults();
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        Console.WriteLine(
            $"engine=synthetic-billion-compact boundaryMode={(boundarySuperset ? "mark-superset" : "predicate-checked")} " +
            $"cache={cacheState} movementFraction={movementFraction:R} " +
            $"totalMs={result.TotalMilliseconds:F2} boundaryMs={result.BoundaryMilliseconds:F2} " +
            $"recomputeMs={result.RecomputeMilliseconds:F2} encodeMs={result.EncodeMilliseconds:F2} " +
            $"changedMarks={result.Changed.Rows} checksum=0x{result.Changed.Checksum:X16} " +
            $"boundaryFacts={result.BoundaryFacts} recomputedFacts={result.RecomputedFacts} " +
            $"logicalGiB={result.LogicalBytes / 1073741824.0:F3} outputMiB={result.OutputBytes / 1048576.0:F2} " +
            $"cpuMs={(cpu1 - cpu0).TotalMilliseconds:F2} pageFaults={faults1 - faults0} " +
            $"physicalReadMiB={(read1 - read0) / 1048576.0:F2} physicalWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"workingSetMiB={ws1 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(ws1 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static ExpectedResult DirectReference(
        SyntheticCompactArtifact artifact, float movementFraction)
    {
        float oldLow = .05f;
        float newLow = oldLow + movementFraction;
        int markCount = artifact.Manifest.MarkCount;
        bool[] candidates = new bool[markCount];
        double[] numerator = new double[markCount];
        double[] denominator = new double[markCount];
        for (int row = 0; row < artifact.Manifest.FactCount; row++)
        {
            bool passOther = true;
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                if (d == Fields.Download) continue;
                float other = artifact.Values[d][row];
                if (other < .05f || other > .95f)
                {
                    passOther = false;
                    break;
                }
            }

            float download = artifact.Values[Fields.Download][row];
            int mark = checked((int)artifact.Mark[row]);
            if (passOther && download >= oldLow && download < newLow)
                candidates[mark] = true;
            if (!passOther || download < newLow || download > .95f) continue;
            float tests = artifact.Tests[row];
            numerator[mark] += (double)(download * tests);
            denominator[mark] += tests;
        }

        long rows = 0;
        ulong checksum = 0;
        for (int mark = 0; mark < markCount; mark++)
        {
            if (!candidates[mark]) continue;
            rows++;
            float value = denominator[mark] > 0
                ? (float)(numerator[mark] / denominator[mark])
                : float.NaN;
            checksum ^= BenchmarkHash.MarkValue((uint)mark, value);
        }
        return new ExpectedResult(rows, checksum);
    }

    public static void Pressure(
        string directory,
        int users,
        float movementFraction,
        bool distinctRanges,
        bool boundarySuperset = false)
    {
        if (users <= 0) throw new ArgumentOutOfRangeException(nameof(users));
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticCompactArtifact(directory);
        var engine = new SyntheticCompactEngine(artifact);
        var workspaces = Enumerable.Range(0, users)
            .Select(_ => new StatelessWorkspace(artifact.Manifest.MarkCount)).ToArray();
        var outcomes = new SyntheticQueryOutcome?[users];
        var errors = new Exception?[users];
        var threads = new Thread[users];
        using var ready = new CountdownEvent(users);
        using var gate = new ManualResetEventSlim(false);

        for (int user = 0; user < users; user++)
        {
            int captured = user;
            threads[user] = new Thread(() =>
            {
                ready.Signal();
                gate.Wait();
                try
                {
                    float baseLower = distinctRanges
                        ? .05f + .80f * captured / users
                        : .05f;
                    outcomes[captured] = engine.Delta(
                        movementFraction, workspaces[captured],
                        checked((uint)captured), baseLower, boundarySuperset);
                }
                catch (Exception ex)
                {
                    errors[captured] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"synthetic-user-{user}"
            };
            threads[user].Start();
        }

        ready.Wait();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        int faults0 = ProcessMetrics.PageFaults();
        long wallStarted = Stopwatch.GetTimestamp();
        gate.Set();
        foreach (Thread thread in threads) thread.Join();
        double wallMs = Stopwatch.GetElapsedTime(wallStarted).TotalMilliseconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        long ws1 = process.WorkingSet64;
        Exception? error = errors.FirstOrDefault(item => item is not null);
        if (error is not null) throw new AggregateException(errors.Where(item => item is not null)!);

        SyntheticQueryOutcome[] complete = outcomes.Select(item => item!).ToArray();
        double[] latency = complete.Select(item => item.TotalMilliseconds).Order().ToArray();
        long logicalBytes = complete.Sum(item => item.LogicalBytes);
        long outputBytes = complete.Sum(item => (long)item.OutputBytes);
        long boundaryFacts = complete.Sum(item => item.BoundaryFacts);
        long recomputedFacts = complete.Sum(item => item.RecomputedFacts);
        Console.WriteLine(
            $"engine=synthetic-billion-compact-pressure boundaryMode={(boundarySuperset ? "mark-superset" : "predicate-checked")} " +
            $"users={users} ranges={(distinctRanges ? "distinct" : "same")} " +
            $"movementFraction={movementFraction:R} burstWallMs={wallMs:F2} " +
            $"p50Ms={Percentile(latency, .50):F2} p95Ms={Percentile(latency, .95):F2} maxMs={latency[^1]:F2} " +
            $"cpuMs={cpuMs:F2} averageLogicalCores={cpuMs / wallMs:F2} " +
            $"boundaryFacts={boundaryFacts} recomputedFacts={recomputedFacts} " +
            $"logicalGiB={logicalBytes / 1073741824.0:F3} outputMiB={outputBytes / 1048576.0:F2} " +
            $"pageFaults={faults} workingSetMiB={ws1 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(ws1 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
