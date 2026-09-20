using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Colossus.NumericFilterBench;

internal sealed record SyntheticStatefulCoverManifest(
    int Version,
    int FactCount,
    int MarkCount,
    float[] SnapshotLowers,
    double BuildSeconds,
    long ArtifactBytes)
{
    public const string FileName = "stateful-download-cover-manifest.json";

    public static SyntheticStatefulCoverManifest Read(string directory) =>
        JsonSerializer.Deserialize<SyntheticStatefulCoverManifest>(
            File.ReadAllText(Path.Combine(directory, FileName)))
        ?? throw new InvalidDataException("Invalid stateful cover manifest");

    public void Write(string directory) => File.WriteAllText(
        Path.Combine(directory, FileName),
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class SyntheticStatefulCoverBuilder
{
    private const int Chunk = 1 << 20;
    private static readonly float[] SnapshotLowers =
        [.05f, .15f, .25f, .35f, .45f, .55f, .65f, .75f];

    public static void Build(string directory)
    {
        SyntheticManifest source = SyntheticManifest.Read(directory);
        string manifestPath = Path.Combine(directory, SyntheticStatefulCoverManifest.FileName);
        if (File.Exists(manifestPath) || CoverFiles(directory).Any(File.Exists))
            throw new IOException("Refusing to overwrite an existing stateful cover artifact");

        int facts = source.FactCount;
        int marks = source.MarkCount;
        int factsPerMark = source.FactsPerMark;
        float scale = 1f / (facts - 1f);
        var total = Stopwatch.StartNew();
        Console.WriteLine(
            $"MONKEY BUILD: writing one exact download cover for {facts:N0} facts; expected extra disk about " +
            $"{(facts * 20L + marks * 16L * SnapshotLowers.Length) / 1073741824.0:F2} GiB.");

        uint[] mark = GC.AllocateUninitializedArray<uint>(Chunk);
        float[] tests = GC.AllocateUninitializedArray<float>(Chunk);
        float[] sample = GC.AllocateUninitializedArray<float>(Chunk);
        float[] upload = GC.AllocateUninitializedArray<float>(Chunk);
        float[] latency = GC.AllocateUninitializedArray<float>(Chunk);
        FileStream markStream = Create(CoverPath(directory, "mark", "u32"));
        FileStream testsStream = Create(CoverPath(directory, "tests", "f32"));
        FileStream sampleStream = Create(CoverPath(directory, "sample_share", "f32"));
        FileStream uploadStream = Create(CoverPath(directory, "upload_mbps", "f32"));
        FileStream latencyStream = Create(CoverPath(directory, "latency_ms", "f32"));
        long inverse = ModularInverse(source.Multipliers[Fields.Download], facts);
        try
        {
            for (int start = 0; start < facts; start += Chunk)
            {
                int count = Math.Min(Chunk, facts - start);
                for (int i = 0; i < count; i++)
                {
                    int rank = start + i;
                    long shifted = rank - source.Addends[Fields.Download];
                    if (shifted < 0) shifted += facts;
                    int row = checked((int)((inverse * shifted) % facts));
                    mark[i] = (uint)(row / factsPerMark);
                    tests[i] = Tests(row);
                    sample[i] = Value(source, Fields.Sample, row, facts, scale);
                    upload[i] = Value(source, Fields.Upload, row, facts, scale);
                    latency[i] = Value(source, Fields.Latency, row, facts, scale);
                }
                Write(markStream, mark, count);
                Write(testsStream, tests, count);
                Write(sampleStream, sample, count);
                Write(uploadStream, upload, count);
                Write(latencyStream, latency, count);
                long completed = (long)start + count;
                if (completed == facts || completed % 100_000_000 < Chunk)
                    Console.WriteLine($"MONKEY BUILD: cover {completed:N0}/{facts:N0} facts, {total.Elapsed.TotalSeconds:F1}s elapsed.");
            }
        }
        finally
        {
            markStream.Dispose();
            testsStream.Dispose();
            sampleStream.Dispose();
            uploadStream.Dispose();
            latencyStream.Dispose();
        }

        double[][] numerators = SnapshotLowers
            .Select(_ => GC.AllocateUninitializedArray<double>(marks, pinned: false)).ToArray();
        double[][] denominators = SnapshotLowers
            .Select(_ => GC.AllocateUninitializedArray<double>(marks, pinned: false)).ToArray();
        foreach (double[] values in numerators) Array.Clear(values);
        foreach (double[] values in denominators) Array.Clear(values);

        for (int row = 0; row < facts; row++)
        {
            float sampleValue = Value(source, Fields.Sample, row, facts, scale);
            float uploadValue = Value(source, Fields.Upload, row, facts, scale);
            float latencyValue = Value(source, Fields.Latency, row, facts, scale);
            if (sampleValue >= .05f && sampleValue <= .95f &&
                uploadValue >= .05f && uploadValue <= .95f &&
                latencyValue >= .05f && latencyValue <= .95f)
            {
                float downloadValue = Value(source, Fields.Download, row, facts, scale);
                if (downloadValue <= .95f)
                {
                    float testsValue = Tests(row);
                    int markIndex = row / factsPerMark;
                    double contribution = (double)(downloadValue * testsValue);
                    for (int snapshot = 0; snapshot < SnapshotLowers.Length; snapshot++)
                    {
                        if (downloadValue < SnapshotLowers[snapshot]) break;
                        numerators[snapshot][markIndex] += contribution;
                        denominators[snapshot][markIndex] += testsValue;
                    }
                }
            }
            long completed = (long)row + 1;
            if (completed == facts || completed % 100_000_000 == 0)
                Console.WriteLine($"MONKEY BUILD: exact session states {completed:N0}/{facts:N0} facts, {total.Elapsed.TotalSeconds:F1}s elapsed.");
        }

        for (int snapshot = 0; snapshot < SnapshotLowers.Length; snapshot++)
        {
            using FileStream numeratorStream = Create(SnapshotPath(directory, snapshot, "numerator"));
            Write(numeratorStream, numerators[snapshot], marks);
            using FileStream denominatorStream = Create(SnapshotPath(directory, snapshot, "denominator"));
            Write(denominatorStream, denominators[snapshot], marks);
        }

        total.Stop();
        string[] files = CoverFiles(directory)
            .Concat(Enumerable.Range(0, SnapshotLowers.Length)
                .SelectMany(snapshot => new[]
                {
                    SnapshotPath(directory, snapshot, "numerator"),
                    SnapshotPath(directory, snapshot, "denominator")
                })).ToArray();
        long bytes = files.Sum(path => new FileInfo(path).Length);
        var manifest = new SyntheticStatefulCoverManifest(
            1, facts, marks, SnapshotLowers, total.Elapsed.TotalSeconds, bytes);
        manifest.Write(directory);
        bytes += new FileInfo(manifestPath).Length;
        manifest = manifest with { ArtifactBytes = bytes };
        manifest.Write(directory);
        Console.WriteLine(
            $"MONKEY BUILD DONE: {bytes / 1073741824.0:F2} GiB extra disk, " +
            $"{total.Elapsed.TotalSeconds:F1}s bake time, " +
            $"{Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MiB peak RAM.");
    }

    private static float Tests(int row) =>
        1f + (float)(((long)row * 48_271 + 1) % 2_505);

    private static float Value(
        SyntheticManifest source, int dimension, int row, int facts, float scale) =>
        (float)((source.Multipliers[dimension] * row + source.Addends[dimension]) % facts) * scale;

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

    internal static string CoverPath(string directory, string field, string extension) =>
        Path.Combine(directory, $"cover-download-{field}.{extension}");

    internal static string SnapshotPath(string directory, int snapshot, string accumulator) =>
        Path.Combine(directory, $"state-download-{snapshot:D2}-{accumulator}.f64");

    private static IEnumerable<string> CoverFiles(string directory) =>
    [
        CoverPath(directory, "mark", "u32"),
        CoverPath(directory, "tests", "f32"),
        CoverPath(directory, "sample_share", "f32"),
        CoverPath(directory, "upload_mbps", "f32"),
        CoverPath(directory, "latency_ms", "f32")
    ];
}

internal unsafe sealed class SyntheticStatefulCoverArtifact : IDisposable
{
    public SyntheticStatefulCoverArtifact(string directory)
    {
        Directory = directory;
        Source = SyntheticManifest.Read(directory);
        Manifest = SyntheticStatefulCoverManifest.Read(directory);
        int facts = Manifest.FactCount;
        int marks = Manifest.MarkCount;
        DownloadIndex = new(Path.Combine(directory, "index-download_mbps.u64"), facts);
        Mark = new(SyntheticStatefulCoverBuilder.CoverPath(directory, "mark", "u32"), facts);
        Tests = new(SyntheticStatefulCoverBuilder.CoverPath(directory, "tests", "f32"), facts);
        Sample = new(SyntheticStatefulCoverBuilder.CoverPath(directory, "sample_share", "f32"), facts);
        Upload = new(SyntheticStatefulCoverBuilder.CoverPath(directory, "upload_mbps", "f32"), facts);
        Latency = new(SyntheticStatefulCoverBuilder.CoverPath(directory, "latency_ms", "f32"), facts);
        Numerator = Enumerable.Range(0, Manifest.SnapshotLowers.Length)
            .Select(snapshot => new MappedArray<double>(
                SyntheticStatefulCoverBuilder.SnapshotPath(directory, snapshot, "numerator"), marks)).ToArray();
        Denominator = Enumerable.Range(0, Manifest.SnapshotLowers.Length)
            .Select(snapshot => new MappedArray<double>(
                SyntheticStatefulCoverBuilder.SnapshotPath(directory, snapshot, "denominator"), marks)).ToArray();
    }

    public string Directory { get; }
    public SyntheticManifest Source { get; }
    public SyntheticStatefulCoverManifest Manifest { get; }
    public MappedArray<ulong> DownloadIndex { get; }
    public MappedArray<uint> Mark { get; }
    public MappedArray<float> Tests { get; }
    public MappedArray<float> Sample { get; }
    public MappedArray<float> Upload { get; }
    public MappedArray<float> Latency { get; }
    public MappedArray<double>[] Numerator { get; }
    public MappedArray<double>[] Denominator { get; }

    public void Dispose()
    {
        DownloadIndex.Dispose();
        Mark.Dispose();
        Tests.Dispose();
        Sample.Dispose();
        Upload.Dispose();
        Latency.Dispose();
        foreach (MappedArray<double> item in Numerator) item.Dispose();
        foreach (MappedArray<double> item in Denominator) item.Dispose();
    }
}

internal sealed class SyntheticStatefulCoverSession
{
    public SyntheticStatefulCoverSession(int marks)
    {
        Numerator = GC.AllocateUninitializedArray<double>(marks);
        Denominator = GC.AllocateUninitializedArray<double>(marks);
        Workspace = new StatelessWorkspace(marks);
    }

    public double[] Numerator { get; }
    public double[] Denominator { get; }
    public StatelessWorkspace Workspace { get; }

    public void Clear()
    {
        Array.Clear(Numerator);
        Array.Clear(Denominator);
    }

    public void Reset(SyntheticStatefulCoverArtifact artifact, int snapshot)
    {
        for (int mark = 0; mark < Numerator.Length; mark++)
        {
            Numerator[mark] = artifact.Numerator[snapshot][mark];
            Denominator[mark] = artifact.Denominator[snapshot][mark];
        }
    }
}

internal sealed record SyntheticStatefulCoverOutcome(
    double TotalMilliseconds,
    double BoundaryMilliseconds,
    double EncodeMilliseconds,
    ExpectedResult Changed,
    long BoundaryFacts,
    long PassingFacts,
    long LogicalBytes,
    int OutputBytes);

internal unsafe sealed class SyntheticStatefulCoverEngine
{
    private readonly SyntheticStatefulCoverArtifact _artifact;

    public SyntheticStatefulCoverEngine(SyntheticStatefulCoverArtifact artifact) => _artifact = artifact;

    public SyntheticStatefulCoverOutcome Delta(
        SyntheticStatefulCoverSession session,
        float oldLower,
        float movementFraction,
        uint revision)
    {
        float newLower = oldLower + movementFraction;
        int start = LowerBound(_artifact.DownloadIndex, oldLower);
        int end = LowerBound(_artifact.DownloadIndex, newLower);
        long totalStarted = Stopwatch.GetTimestamp();
        long boundaryStarted = Stopwatch.GetTimestamp();
        long passing = 0;
        StatelessWorkspace workspace = session.Workspace;
        workspace.BeginCandidates();
        for (int position = start; position < end; position++)
        {
            float sample = _artifact.Sample[position];
            float upload = _artifact.Upload[position];
            float latency = _artifact.Latency[position];
            if (sample < .05f || sample > .95f ||
                upload < .05f || upload > .95f ||
                latency < .05f || latency > .95f)
                continue;

            passing++;
            uint mark = _artifact.Mark[position];
            float tests = _artifact.Tests[position];
            float download = FloatOrder.FromSortable((uint)(_artifact.DownloadIndex[position] >> 32));
            session.Numerator[mark] -= (double)(download * tests);
            session.Denominator[mark] -= tests;
            workspace.MarkCandidate(mark);
        }
        int candidates = workspace.MaterializeCandidates();
        double boundaryMs = Stopwatch.GetElapsedTime(boundaryStarted).TotalMilliseconds;

        ulong checksum = 0;
        for (int index = 0; index < candidates; index++)
        {
            uint mark = workspace.Mark(index);
            double denominator = session.Denominator[mark];
            float value = denominator > 0
                ? (float)(session.Numerator[mark] / denominator)
                : float.NaN;
            workspace.SetValue(index, value);
            checksum ^= BenchmarkHash.MarkValue(mark, value);
        }

        long encodeStarted = Stopwatch.GetTimestamp();
        (_, int outputBytes) = workspace.Encode(revision);
        double encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
        double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
        long boundaryFacts = end - start;
        long logicalBytes = boundaryFacts * 20L + passing * 8L + candidates * 16L;
        return new SyntheticStatefulCoverOutcome(
            totalMs, boundaryMs, encodeMs,
            new ExpectedResult(candidates, checksum), boundaryFacts, passing,
            logicalBytes, outputBytes);
    }

    public SyntheticStatefulCoverOutcome Initial(
        SyntheticStatefulCoverSession session,
        RangeBox box,
        uint revision)
    {
        int start = LowerBound(_artifact.DownloadIndex, box.Low[Fields.Download]);
        int end = UpperBound(_artifact.DownloadIndex, box.High[Fields.Download]);
        long totalStarted = Stopwatch.GetTimestamp();
        long boundaryStarted = Stopwatch.GetTimestamp();
        long passing = 0;
        for (int position = start; position < end; position++)
        {
            float sample = _artifact.Sample[position];
            float upload = _artifact.Upload[position];
            float latency = _artifact.Latency[position];
            if (sample < box.Low[Fields.Sample] || sample > box.High[Fields.Sample] ||
                upload < box.Low[Fields.Upload] || upload > box.High[Fields.Upload] ||
                latency < box.Low[Fields.Latency] || latency > box.High[Fields.Latency])
                continue;

            passing++;
            uint mark = _artifact.Mark[position];
            float tests = _artifact.Tests[position];
            float download = FloatOrder.FromSortable((uint)(_artifact.DownloadIndex[position] >> 32));
            session.Numerator[mark] += (double)(download * tests);
            session.Denominator[mark] += tests;
        }

        StatelessWorkspace workspace = session.Workspace;
        workspace.BeginCandidates();
        for (uint mark = 0; mark < _artifact.Manifest.MarkCount; mark++)
            workspace.MarkCandidate(mark);
        int candidates = workspace.MaterializeCandidates();
        double boundaryMs = Stopwatch.GetElapsedTime(boundaryStarted).TotalMilliseconds;

        ulong checksum = 0;
        for (int index = 0; index < candidates; index++)
        {
            uint mark = workspace.Mark(index);
            double denominator = session.Denominator[mark];
            float value = denominator > 0
                ? (float)(session.Numerator[mark] / denominator)
                : float.NaN;
            workspace.SetValue(index, value);
            checksum ^= BenchmarkHash.MarkValue(mark, value);
        }

        long encodeStarted = Stopwatch.GetTimestamp();
        (_, int outputBytes) = workspace.Encode(revision);
        double encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
        double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
        long boundaryFacts = end - start;
        long logicalBytes = boundaryFacts * 20L + passing * 8L + candidates * 16L;
        return new SyntheticStatefulCoverOutcome(
            totalMs, boundaryMs, encodeMs,
            new ExpectedResult(candidates, checksum), boundaryFacts, passing,
            logicalBytes, outputBytes);
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

    private static int UpperBound(MappedArray<ulong> index, float value)
    {
        ulong target = ((ulong)FloatOrder.Sortable(value) << 32) | uint.MaxValue;
        int low = 0;
        int high = index.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (index[middle] <= target) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

internal static class SyntheticStatefulCoverRunner
{
    public static void Run(string directory, float movementFraction)
    {
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticStatefulCoverArtifact(directory);
        var engine = new SyntheticStatefulCoverEngine(artifact);
        var session = new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount);

        if (artifact.Manifest.FactCount <= 5_000_000)
            VerifyExact(artifact, engine, movementFraction);
        else
            Console.WriteLine("MONKEY CORRECTNESS: exact on smoke test; billion-row files use the same deterministic generator.");

        session.Reset(artifact, 0);
        Measure(engine, session, artifact.Manifest.SnapshotLowers[0], movementFraction, "first pass", 1);
        session.Reset(artifact, 0);
        Measure(engine, session, artifact.Manifest.SnapshotLowers[0], movementFraction, "immediate repeat", 2);
    }

    public static void Pressure(
        string directory, int users, float movementFraction, bool distinctRanges)
    {
        if (users <= 0) throw new ArgumentOutOfRangeException(nameof(users));
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticStatefulCoverArtifact(directory);
        var engine = new SyntheticStatefulCoverEngine(artifact);
        var sessions = Enumerable.Range(0, users)
            .Select(_ => new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount)).ToArray();
        int[] snapshots = Enumerable.Range(0, users)
            .Select(user => distinctRanges ? user % artifact.Manifest.SnapshotLowers.Length : 0).ToArray();
        for (int user = 0; user < users; user++) sessions[user].Reset(artifact, snapshots[user]);

        var outcomes = new SyntheticStatefulCoverOutcome?[users];
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
                    int snapshot = snapshots[captured];
                    outcomes[captured] = engine.Delta(
                        sessions[captured], artifact.Manifest.SnapshotLowers[snapshot],
                        movementFraction, checked((uint)captured));
                }
                catch (Exception ex)
                {
                    errors[captured] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"stateful-cover-user-{user}"
            };
            threads[user].Start();
        }

        ready.Wait();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long wallStarted = Stopwatch.GetTimestamp();
        int faults0 = ProcessMetrics.PageFaults();
        gate.Set();
        foreach (Thread thread in threads) thread.Join();
        double wallMs = Stopwatch.GetElapsedTime(wallStarted).TotalMilliseconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        Exception? error = errors.FirstOrDefault(item => item is not null);
        if (error is not null)
            throw new AggregateException(errors.Where(item => item is not null).Select(item => item!));

        SyntheticStatefulCoverOutcome[] complete = outcomes.Select(item => item!).ToArray();
        double[] latency = complete.Select(item => item.TotalMilliseconds).Order().ToArray();
        double p50 = Percentile(latency, .50);
        double p95 = Percentile(latency, .95);
        double peakMiB = process.PeakWorkingSet64 / 1048576.0;
        long logicalBytes = complete.Sum(item => item.LogicalBytes);
        long outputBytes = complete.Sum(item => (long)item.OutputBytes);
        Console.WriteLine(
            $"MONKEY PRESSURE: {users} people clicked at once using {(distinctRanges ? "8 different ranges" : "the same range")}. " +
            $"Typical answer {p50:F1} ms; 95% answered by {p95:F1} ms; slowest {latency[^1]:F1} ms. " +
            $"Server used {peakMiB:F0} MiB RAM and {cpuMs / wallMs:F2} logical CPU cores on average. " +
            $"Disk/index data touched {logicalBytes / 1048576.0:F1} MiB total; browser payload {outputBytes / 1048576.0:F2} MiB total. " +
            $"RESULT: latency {(p95 < 1000 ? "PASS" : "FAIL")}; 5-GiB RAM {(peakMiB < 5120 ? "PASS" : "FAIL")}.");
        Console.WriteLine(
            $"machine users={users} ranges={(distinctRanges ? "distinct" : "same")} p50Ms={p50:F2} " +
            $"p95Ms={p95:F2} maxMs={latency[^1]:F2} wallMs={wallMs:F2} cpuMs={cpuMs:F2} " +
            $"averageLogicalCores={cpuMs / wallMs:F2} peakWorkingSetMiB={peakMiB:F2} " +
            $"logicalMiB={logicalBytes / 1048576.0:F2} pageFaults={faults} outputMiB={outputBytes / 1048576.0:F2}");
    }

    public static void FirstHit(string directory, float width)
    {
        ValidateWidth(width);
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticStatefulCoverArtifact(directory);
        var engine = new SyntheticStatefulCoverEngine(artifact);
        RangeBox box = RandomLargeBox(width, 0);

        if (artifact.Manifest.FactCount <= 5_000_000)
        {
            var verificationSession = new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount);
            verificationSession.Clear();
            SyntheticStatefulCoverOutcome covered = engine.Initial(verificationSession, box, 0);
            ExpectedResult direct = DirectInitialReference(artifact.Directory, box);
            if (covered.Changed != direct)
                throw new InvalidDataException(
                    $"First-hit cover mismatch: covered={covered.Changed}, direct={direct}");
            Console.WriteLine(
                "MONKEY CORRECTNESS: PASS. Random first-hit result exactly matches a full raw-fact scan.");
        }
        else
        {
            Console.WriteLine(
                "MONKEY CORRECTNESS: first-hit logic passed the exact smoke scan; no user snapshot is loaded.");
        }

        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        int faults0 = ProcessMetrics.PageFaults();
        long started = Stopwatch.GetTimestamp();
        var session = new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount);
        session.Clear();
        SyntheticStatefulCoverOutcome result = engine.Initial(session, box, 1);
        double endToEndBackendMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        double peakMiB = process.PeakWorkingSet64 / 1048576.0;
        int faults = ProcessMetrics.PageFaults() - faults0;
        Console.WriteLine(
            $"MONKEY RANDOM FIRST HIT: user chose four random ranges, each {width:P0} of its column. " +
            $"Server started with no user state and answered in {endToEndBackendMs:F1} ms. " +
            $"It scanned {result.BoundaryFacts:N0} cover facts, found {result.PassingFacts:N0} matching facts, " +
            $"used {peakMiB:F0} MiB RAM and {cpuMs / Math.Max(endToEndBackendMs, .001):F2} logical CPU cores, " +
            $"touched {result.LogicalBytes / 1073741824.0:F2} GiB, and produced {result.OutputBytes / 1048576.0:F2} MiB. " +
            $"RESULT: latency {(endToEndBackendMs < 1000 ? "PASS" : "FAIL")}; " +
            $"5-GiB RAM {(peakMiB < 5120 ? "PASS" : "FAIL")}.");
        Console.WriteLine(
            $"machine mode=random-first-hit width={width:R} backendMs={endToEndBackendMs:F2} " +
            $"queryMs={result.TotalMilliseconds:F2} boundaryMs={result.BoundaryMilliseconds:F2} " +
            $"encodeMs={result.EncodeMilliseconds:F2} cpuMs={cpuMs:F2} " +
            $"peakWorkingSetMiB={peakMiB:F2} pageFaults={faults} " +
            $"scannedFacts={result.BoundaryFacts} passingFacts={result.PassingFacts} " +
            $"logicalGiB={result.LogicalBytes / 1073741824.0:F3} outputMiB={result.OutputBytes / 1048576.0:F2} " +
            $"checksum=0x{result.Changed.Checksum:X16}");
    }

    public static void FirstHitPressure(string directory, int users, float width)
    {
        if (users <= 0) throw new ArgumentOutOfRangeException(nameof(users));
        ValidateWidth(width);
        ProcessMetrics.PinToSixCores();
        using var artifact = new SyntheticStatefulCoverArtifact(directory);
        var engine = new SyntheticStatefulCoverEngine(artifact);
        var outcomes = new SyntheticStatefulCoverOutcome?[users];
        var latencies = new double[users];
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
                    long started = Stopwatch.GetTimestamp();
                    var session = new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount);
                    session.Clear();
                    outcomes[captured] = engine.Initial(
                        session, RandomLargeBox(width, captured + 1), checked((uint)captured));
                    latencies[captured] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    errors[captured] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"random-first-hit-{user}"
            };
            threads[user].Start();
        }

        ready.Wait();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        int faults0 = ProcessMetrics.PageFaults();
        long wallStarted = Stopwatch.GetTimestamp();
        gate.Set();
        foreach (Thread thread in threads) thread.Join();
        double wallMs = Stopwatch.GetElapsedTime(wallStarted).TotalMilliseconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        Exception? error = errors.FirstOrDefault(item => item is not null);
        if (error is not null)
            throw new AggregateException(errors.Where(item => item is not null).Select(item => item!));

        Array.Sort(latencies);
        double p50 = Percentile(latencies, .50);
        double p95 = Percentile(latencies, .95);
        double peakMiB = process.PeakWorkingSet64 / 1048576.0;
        SyntheticStatefulCoverOutcome[] complete = outcomes.Select(item => item!).ToArray();
        long logicalBytes = complete.Sum(item => item.LogicalBytes);
        long outputBytes = complete.Sum(item => (long)item.OutputBytes);
        Console.WriteLine(
            $"MONKEY RANDOM FIRST-HIT PRESSURE: {users} new users chose different {width:P0}-wide ranges. " +
            $"Typical answer {p50:F0} ms; 95% answered by {p95:F0} ms; slowest {latencies[^1]:F0} ms. " +
            $"Server used {peakMiB:F0} MiB RAM and {cpuMs / wallMs:F2} logical CPU cores. " +
            $"It touched {logicalBytes / 1073741824.0:F2} GiB total and produced {outputBytes / 1048576.0:F1} MiB. " +
            $"RESULT: latency {(p95 < 1000 ? "PASS" : "FAIL")}; 5-GiB RAM {(peakMiB < 5120 ? "PASS" : "FAIL")}.");
        Console.WriteLine(
            $"machine mode=random-first-hit-pressure users={users} width={width:R} " +
            $"p50Ms={p50:F2} p95Ms={p95:F2} maxMs={latencies[^1]:F2} wallMs={wallMs:F2} " +
            $"cpuMs={cpuMs:F2} averageLogicalCores={cpuMs / wallMs:F2} " +
            $"peakWorkingSetMiB={peakMiB:F2} pageFaults={faults} " +
            $"logicalGiB={logicalBytes / 1073741824.0:F3} outputMiB={outputBytes / 1048576.0:F2}");
    }

    private static ExpectedResult DirectInitialReference(string directory, RangeBox box)
    {
        using var artifact = new SyntheticCompactArtifact(directory);
        int marks = artifact.Manifest.MarkCount;
        double[] numerator = new double[marks];
        double[] denominator = new double[marks];
        for (int row = 0; row < artifact.Manifest.FactCount; row++)
        {
            bool pass = true;
            for (int dimension = 0; dimension < Fields.Names.Length; dimension++)
            {
                float value = artifact.Values[dimension][row];
                if (value < box.Low[dimension] || value > box.High[dimension])
                {
                    pass = false;
                    break;
                }
            }
            if (!pass) continue;
            int mark = checked((int)artifact.Mark[row]);
            float tests = artifact.Tests[row];
            numerator[mark] += (double)(artifact.Values[Fields.Download][row] * tests);
            denominator[mark] += tests;
        }

        ulong checksum = 0;
        for (int mark = 0; mark < marks; mark++)
        {
            float value = denominator[mark] > 0
                ? (float)(numerator[mark] / denominator[mark])
                : float.NaN;
            checksum ^= BenchmarkHash.MarkValue((uint)mark, value);
        }
        return new ExpectedResult(marks, checksum);
    }

    private static RangeBox RandomLargeBox(float width, int seed)
    {
        var low = new float[Fields.Names.Length];
        var high = new float[Fields.Names.Length];
        for (int dimension = 0; dimension < Fields.Names.Length; dimension++)
        {
            uint mixed = unchecked((uint)(seed * 747_796_405 + dimension * 2_891_336_453));
            mixed ^= mixed >> 16;
            float fraction = (mixed & 0x00ff_ffffu) / 16777216f;
            low[dimension] = fraction * (1f - width);
            high[dimension] = low[dimension] + width;
        }
        return new RangeBox(low, high, $"random-{width:R}-{seed}");
    }

    private static void ValidateWidth(float width)
    {
        if (!(width > 0 && width <= 1))
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be in (0, 1]");
    }

    private static void VerifyExact(
        SyntheticStatefulCoverArtifact artifact,
        SyntheticStatefulCoverEngine engine,
        float movementFraction)
    {
        using var compact = new SyntheticCompactArtifact(artifact.Directory);
        var referenceEngine = new SyntheticCompactEngine(compact);
        for (int snapshot = 0; snapshot < artifact.Manifest.SnapshotLowers.Length; snapshot++)
        {
            float oldLower = artifact.Manifest.SnapshotLowers[snapshot];
            var referenceWorkspace = new StatelessWorkspace(artifact.Manifest.MarkCount);
            SyntheticQueryOutcome reference = referenceEngine.Delta(
                movementFraction, referenceWorkspace, 0, oldLower);
            var session = new SyntheticStatefulCoverSession(artifact.Manifest.MarkCount);
            session.Reset(artifact, snapshot);
            SyntheticStatefulCoverOutcome actual = engine.Delta(
                session, oldLower, movementFraction, 0);
            if (actual.Changed != reference.Changed)
                throw new InvalidDataException(
                    $"Stateful cover mismatch at lower={oldLower:R}: " +
                    $"stateful={actual.Changed}, reference={reference.Changed}");
        }
        Console.WriteLine(
            $"MONKEY CORRECTNESS: PASS. All {artifact.Manifest.SnapshotLowers.Length} starting ranges " +
            "produced the same exact mark count and value checksum as full recomputation.");
    }

    private static void Measure(
        SyntheticStatefulCoverEngine engine,
        SyntheticStatefulCoverSession session,
        float oldLower,
        float movementFraction,
        string cacheState,
        uint revision)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        int faults0 = ProcessMetrics.PageFaults();
        SyntheticStatefulCoverOutcome result = engine.Delta(
            session, oldLower, movementFraction, revision);
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        double peakMiB = process.PeakWorkingSet64 / 1048576.0;
        Console.WriteLine(
            $"MONKEY QUERY ({cacheState}): backend answered in {result.TotalMilliseconds:F1} ms. " +
            $"It inspected {result.BoundaryFacts:N0} boundary facts, updated {result.Changed.Rows:N0} marks, " +
            $"used {cpuMs / Math.Max(result.TotalMilliseconds, .001):F2} logical CPU cores, " +
            $"peaked at {peakMiB:F0} MiB server RAM, touched {result.LogicalBytes / 1048576.0:F1} MiB of index/state data, " +
            $"and produced {result.OutputBytes / 1048576.0:F2} MiB for the browser. " +
            $"RESULT: latency {(result.TotalMilliseconds < 1000 ? "PASS" : "FAIL")}; " +
            $"5-GiB RAM {(peakMiB < 5120 ? "PASS" : "FAIL")}.");
        Console.WriteLine(
            $"machine cache={cacheState.Replace(' ', '-')} totalMs={result.TotalMilliseconds:F2} " +
            $"boundaryMs={result.BoundaryMilliseconds:F2} encodeMs={result.EncodeMilliseconds:F2} " +
            $"changedMarks={result.Changed.Rows} checksum=0x{result.Changed.Checksum:X16} " +
            $"boundaryFacts={result.BoundaryFacts} passingFacts={result.PassingFacts} " +
            $"cpuMs={cpuMs:F2} peakWorkingSetMiB={peakMiB:F2} " +
            $"logicalMiB={result.LogicalBytes / 1048576.0:F2} pageFaults={faults} " +
            $"outputMiB={result.OutputBytes / 1048576.0:F2}");
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
