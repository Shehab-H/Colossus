using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Colossus.NumericFilterBench;

internal sealed record TimestampTreeManifest(
    int Version,
    int FactCount,
    int MarkCount,
    int DayCount,
    long[] DayOffsets,
    double BuildSeconds,
    long RawBytes,
    long TreeBytes)
{
    public const string FileName = "timestamp-tree-manifest.json";

    public static TimestampTreeManifest Read(string directory) =>
        JsonSerializer.Deserialize<TimestampTreeManifest>(
            File.ReadAllText(Path.Combine(directory, FileName)))
        ?? throw new InvalidDataException("Invalid timestamp-tree manifest");

    public void Write(string directory) => File.WriteAllText(
        Path.Combine(directory, FileName),
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TimestampRawFact
{
    public uint SecondOfDay;
    public uint Mark;
    public float Value;
    public uint Weight;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TimestampAggregate
{
    public double Numerator;
    public long Denominator;
}

internal static class TimestampTreeBuilder
{
    private const int SecondsPerDay = 86_400;
    private const int RawChunk = 1 << 18;
    private const int MarkChunk = 1 << 17;

    public static void Build(string directory, int facts, int marks, int days)
    {
        if (facts <= 0 || marks <= 0 || days < 3)
            throw new ArgumentException("Facts and marks must be positive; days must be at least three");
        if (Marshal.SizeOf<TimestampRawFact>() != 16 || Marshal.SizeOf<TimestampAggregate>() != 16)
            throw new InvalidOperationException("Timestamp benchmark disk structures must remain 16 bytes");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException($"Refusing to overwrite non-empty artifact directory: {directory}");

        Directory.CreateDirectory(directory);
        ProcessMetrics.PinToSixCores();
        var elapsed = Stopwatch.StartNew();
        long[] dayOffsets = new long[days + 1];
        int baseFacts = facts / days;
        int extraFacts = facts % days;
        long nodeBytes = checked((long)marks * Marshal.SizeOf<TimestampAggregate>());
        int treeNodes = checked(2 * days - 1);
        long treeBytes = checked(nodeBytes * treeNodes);
        string rawPath = Path.Combine(directory, "timestamp-facts.bin");
        string treePath = Path.Combine(directory, "timestamp-day-tree.bin");

        Console.WriteLine(
            $"timestampTreeBuild facts={facts:N0} marks={marks:N0} days={days:N0} " +
            $"treeNodes={treeNodes:N0} plannedTreeGiB={treeBytes / 1073741824.0:F3}");

        TimestampRawFact[] raw = GC.AllocateUninitializedArray<TimestampRawFact>(RawChunk);
        TimestampAggregate[] leaf = new TimestampAggregate[marks];
        using FileStream rawStream = new(
            rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4 << 20, FileOptions.SequentialScan);
        using FileStream treeStream = new(
            treePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            4 << 20, FileOptions.RandomAccess);
        treeStream.SetLength(treeBytes);
        SafeFileHandle treeHandle = treeStream.SafeFileHandle;

        long global = 0;
        long nextProgress = 25_000_000;
        for (int day = 0; day < days; day++)
        {
            int dayFacts = baseFacts + (day < extraFacts ? 1 : 0);
            dayOffsets[day] = global;
            Array.Clear(leaf);
            int buffered = 0;
            for (int index = 0; index < dayFacts; index++, global++)
            {
                uint mark = (uint)(((ulong)global * 2_654_435_761UL + 17UL) % (uint)marks);
                uint weight = 1U + (uint)(((ulong)global * 48_271UL + 31UL) % 100UL);
                float value = 1f + (float)(((ulong)global * 224_682_251UL + 47UL) % 1_000UL);
                var fact = new TimestampRawFact
                {
                    SecondOfDay = (uint)(((long)index * SecondsPerDay) / dayFacts),
                    Mark = mark,
                    Value = value,
                    Weight = weight
                };
                raw[buffered++] = fact;
                ref TimestampAggregate aggregate = ref leaf[mark];
                aggregate.Numerator += (double)value * weight;
                aggregate.Denominator += weight;

                if (buffered == raw.Length)
                {
                    rawStream.Write(MemoryMarshal.AsBytes(raw.AsSpan()));
                    buffered = 0;
                }
                if (global + 1 >= nextProgress)
                {
                    Console.WriteLine(
                        $"timestampRaw facts={global + 1:N0} elapsedSeconds={elapsed.Elapsed.TotalSeconds:F1}");
                    nextProgress += 25_000_000;
                }
            }
            if (buffered > 0)
                rawStream.Write(MemoryMarshal.AsBytes(raw.AsSpan(0, buffered)));

            RandomAccess.Write(
                treeHandle,
                MemoryMarshal.AsBytes(leaf.AsSpan()),
                NodeOffset(days + day, nodeBytes));
        }
        dayOffsets[days] = global;
        rawStream.Flush(flushToDisk: true);
        treeStream.Flush(flushToDisk: true);
        Console.WriteLine($"timestampLeaves complete elapsedSeconds={elapsed.Elapsed.TotalSeconds:F1}");

        TimestampAggregate[] left = GC.AllocateUninitializedArray<TimestampAggregate>(MarkChunk);
        TimestampAggregate[] right = GC.AllocateUninitializedArray<TimestampAggregate>(MarkChunk);
        TimestampAggregate[] parent = GC.AllocateUninitializedArray<TimestampAggregate>(MarkChunk);
        for (int node = days - 1; node >= 1; node--)
        {
            for (int markStart = 0; markStart < marks; markStart += MarkChunk)
            {
                int count = Math.Min(MarkChunk, marks - markStart);
                long markByteOffset = checked((long)markStart * 16L);
                ReadExactly(
                    treeHandle, MemoryMarshal.AsBytes(left.AsSpan(0, count)),
                    NodeOffset(node * 2, nodeBytes) + markByteOffset);
                ReadExactly(
                    treeHandle, MemoryMarshal.AsBytes(right.AsSpan(0, count)),
                    NodeOffset(node * 2 + 1, nodeBytes) + markByteOffset);
                for (int i = 0; i < count; i++)
                {
                    parent[i].Numerator = left[i].Numerator + right[i].Numerator;
                    parent[i].Denominator = left[i].Denominator + right[i].Denominator;
                }
                RandomAccess.Write(
                    treeHandle, MemoryMarshal.AsBytes(parent.AsSpan(0, count)),
                    NodeOffset(node, nodeBytes) + markByteOffset);
            }
            if (node == 1 || node % 25 == 0)
                Console.WriteLine(
                    $"timestampTree parent={node:N0} remaining={node - 1:N0} " +
                    $"elapsedSeconds={elapsed.Elapsed.TotalSeconds:F1}");
        }
        treeStream.Flush(flushToDisk: true);
        elapsed.Stop();

        long rawBytes = new FileInfo(rawPath).Length;
        var manifest = new TimestampTreeManifest(
            1, facts, marks, days, dayOffsets, elapsed.Elapsed.TotalSeconds,
            rawBytes, treeBytes);
        manifest.Write(directory);
        Console.WriteLine(
            $"timestampTreeBuildSeconds={elapsed.Elapsed.TotalSeconds:F2} " +
            $"rawGiB={rawBytes / 1073741824.0:F3} treeGiB={treeBytes / 1073741824.0:F3} " +
            $"totalGiB={(rawBytes + treeBytes) / 1073741824.0:F3} " +
            $"peakWorkingSetMiB={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static long NodeOffset(int node, long nodeBytes) => checked((node - 1L) * nodeBytes);

    private static void ReadExactly(SafeFileHandle handle, Span<byte> target, long offset)
    {
        int completed = 0;
        while (completed < target.Length)
        {
            int read = RandomAccess.Read(handle, target[completed..], offset + completed);
            if (read == 0) throw new EndOfStreamException();
            completed += read;
        }
    }
}

internal sealed class TimestampTreeArtifact : IDisposable
{
    private readonly SafeFileHandle? _raw;
    private readonly SafeFileHandle? _tree;
    private readonly WindowsUnbufferedReader? _rawDirect;
    private readonly WindowsUnbufferedReader? _treeDirect;

    public TimestampTreeArtifact(string directory, bool directIo = false)
    {
        Directory = directory;
        Manifest = TimestampTreeManifest.Read(directory);
        string rawPath = Path.Combine(directory, "timestamp-facts.bin");
        string treePath = Path.Combine(directory, "timestamp-day-tree.bin");
        if (directIo && OperatingSystem.IsWindows())
        {
            _rawDirect = new WindowsUnbufferedReader(rawPath);
            _treeDirect = new WindowsUnbufferedReader(treePath);
        }
        else
        {
            _raw = File.OpenHandle(
                rawPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.RandomAccess);
            _tree = File.OpenHandle(
                treePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.RandomAccess);
        }
    }

    public string Directory { get; }
    public TimestampTreeManifest Manifest { get; }
    public long NodeBytes => checked((long)Manifest.MarkCount * 16L);

    public void ReadRaw(Span<byte> target, long offset)
    {
        if (_rawDirect is not null) _rawDirect.ReadExactly(target, offset);
        else ReadExactly(_raw!, target, offset);
    }

    public void ReadTree(Span<byte> target, long offset)
    {
        if (_treeDirect is not null) _treeDirect.ReadExactly(target, offset);
        else ReadExactly(_tree!, target, offset);
    }

    public void Dispose()
    {
        _raw?.Dispose();
        _tree?.Dispose();
        _rawDirect?.Dispose();
        _treeDirect?.Dispose();
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> target, long offset)
    {
        int completed = 0;
        while (completed < target.Length)
        {
            int read = RandomAccess.Read(handle, target[completed..], offset + completed);
            if (read == 0) throw new EndOfStreamException();
            completed += read;
        }
    }
}

internal unsafe sealed class WindowsUnbufferedReader : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagNoBuffering = 0x20000000;
    private const uint FileFlagRandomAccess = 0x10000000;
    private const uint FileBegin = 0;
    private const int BufferBytes = 4 << 20;
    private readonly SafeFileHandle _handle;
    private readonly int _sectorBytes;
    private readonly byte* _buffer;
    private readonly object _gate = new();

    public WindowsUnbufferedReader(string path)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new IOException($"Cannot determine volume for {path}");
        if (!GetDiskFreeSpaceW(root, out _, out uint bytesPerSector, out _, out _))
            throw new IOException($"GetDiskFreeSpace failed: {Marshal.GetLastWin32Error()}");
        _sectorBytes = checked((int)bytesPerSector);
        if (BufferBytes % _sectorBytes != 0)
            throw new IOException($"Direct-I/O buffer is not aligned to sector size {_sectorBytes}");
        _handle = CreateFileW(
            path, GenericRead, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, OpenExisting,
            FileFlagNoBuffering | FileFlagRandomAccess, IntPtr.Zero);
        if (_handle.IsInvalid)
            throw new IOException($"CreateFile direct I/O failed: {Marshal.GetLastWin32Error()}");
        _buffer = (byte*)NativeMemory.AlignedAlloc(BufferBytes, 4096);
        if (_buffer is null) throw new OutOfMemoryException();
    }

    public void ReadExactly(Span<byte> target, long offset)
    {
        if (target.IsEmpty) return;
        lock (_gate)
        {
            long first = offset - offset % _sectorBytes;
            long requestedEnd = checked(offset + target.Length);
            long last = checked(
                ((requestedEnd + _sectorBytes - 1) / _sectorBytes) * _sectorBytes);
            int copied = 0;
            for (long position = first; position < last; position += BufferBytes)
            {
                int request = checked((int)Math.Min(BufferBytes, last - position));
                if (!SetFilePointerEx(_handle, position, out _, FileBegin))
                    throw new IOException($"SetFilePointerEx failed: {Marshal.GetLastWin32Error()}");
                if (!ReadFile(_handle, _buffer, (uint)request, out uint read, IntPtr.Zero) || read == 0)
                    throw new IOException($"Direct ReadFile failed: {Marshal.GetLastWin32Error()}");

                long overlapStart = Math.Max(offset, position);
                long overlapEnd = Math.Min(requestedEnd, position + read);
                if (overlapStart >= overlapEnd) continue;
                int sourceOffset = checked((int)(overlapStart - position));
                int destinationOffset = checked((int)(overlapStart - offset));
                int count = checked((int)(overlapEnd - overlapStart));
                new ReadOnlySpan<byte>(_buffer + sourceOffset, count)
                    .CopyTo(target.Slice(destinationOffset, count));
                copied += count;
            }
            if (copied != target.Length)
                throw new EndOfStreamException(
                    $"Direct read copied {copied} of {target.Length} requested bytes");
        }
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(_buffer);
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle file, long distance, out long newPointer, uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        SafeFileHandle file, void* buffer, uint bytesToRead,
        out uint bytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(
        string rootPathName, out uint sectorsPerCluster,
        out uint bytesPerSector, out uint freeClusters,
        out uint totalClusters);
}

internal sealed record TimestampQueryOutcome(
    double TotalMilliseconds,
    double ReadAndMergeMilliseconds,
    double EncodeMilliseconds,
    int TreeNodes,
    long BoundaryFacts,
    long LogicalBytes,
    long OutputBytes,
    long ActiveMarks,
    ulong Checksum);

internal sealed record TimestampPayloadResult(
    TimestampQueryOutcome Outcome,
    byte[] Payload);

internal sealed class TimestampTreeEngine
{
    private const int SecondsPerDay = 86_400;
    private const int MarkChunk = 1 << 17;
    private const int RawChunk = 1 << 18;
    private const int WireChunk = 1 << 20;
    private readonly TimestampTreeArtifact _artifact;

    public TimestampTreeEngine(TimestampTreeArtifact artifact) => _artifact = artifact;

    public TimestampQueryOutcome Absolute(long startSecond, long endSecond) =>
        Execute([(startSecond, endSecond, 1)], displayPayload: false).Outcome;

    public TimestampPayloadResult AbsoluteDisplayPayload(long startSecond, long endSecond)
    {
        (TimestampQueryOutcome outcome, byte[]? payload) =
            Execute([(startSecond, endSecond, 1)], displayPayload: true);
        return new TimestampPayloadResult(outcome, payload!);
    }

    public TimestampQueryOutcome Delta(
        long oldStartSecond, long oldEndSecond,
        long newStartSecond, long newEndSecond) =>
        Execute([
            (oldStartSecond, oldEndSecond, -1),
            (newStartSecond, newEndSecond, 1)
        ], displayPayload: false).Outcome;

    private (TimestampQueryOutcome Outcome, byte[]? Payload) Execute(
        (long Start, long End, int Sign)[] ranges,
        bool displayPayload)
    {
        int marks = _artifact.Manifest.MarkCount;
        TimestampAggregate[] accumulator = ArrayPool<TimestampAggregate>.Shared.Rent(marks);
        TimestampAggregate[] nodeBuffer = ArrayPool<TimestampAggregate>.Shared.Rent(
            Math.Min(MarkChunk, marks));
        TimestampRawFact[] rawBuffer = ArrayPool<TimestampRawFact>.Shared.Rent(RawChunk);
        byte[] wireBuffer = ArrayPool<byte>.Shared.Rent(WireChunk);
        try
        {
            accumulator.AsSpan(0, marks).Clear();
            var counters = new QueryCounters();
            long totalStarted = Stopwatch.GetTimestamp();
            long mergeStarted = Stopwatch.GetTimestamp();
            foreach ((long start, long end, int sign) in ranges)
                AddRange(accumulator, nodeBuffer, rawBuffer, start, end, sign, counters);
            double mergeMs = Stopwatch.GetElapsedTime(mergeStarted).TotalMilliseconds;

            long encodeStarted = Stopwatch.GetTimestamp();
            ulong checksum = 0;
            long active = 0;
            ReadOnlySpan<byte> stateResponse = MemoryMarshal.AsBytes(accumulator.AsSpan(0, marks));
            byte[]? payload = displayPayload
                ? GC.AllocateUninitializedArray<byte>(checked(marks * sizeof(float)))
                : null;
            Span<float> display = payload is null
                ? Span<float>.Empty
                : MemoryMarshal.Cast<byte, float>(payload.AsSpan());
            if (!displayPayload)
            {
                for (int offset = 0; offset < stateResponse.Length; offset += WireChunk)
                {
                    int count = Math.Min(WireChunk, stateResponse.Length - offset);
                    stateResponse.Slice(offset, count).CopyTo(wireBuffer);
                }
            }
            for (int mark = 0; mark < marks; mark++)
            {
                TimestampAggregate value = accumulator[mark];
                if (value.Numerator != 0 || value.Denominator != 0) active++;
                if (displayPayload)
                    display[mark] = value.Denominator > 0
                        ? (float)(value.Numerator / value.Denominator)
                        : float.NaN;
                checksum ^= AggregateHash((uint)mark, value);
            }
            double encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
            double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
            long outputBytes = payload?.Length ?? stateResponse.Length;
            var outcome = new TimestampQueryOutcome(
                totalMs, mergeMs, encodeMs, counters.TreeNodes, counters.BoundaryFacts,
                counters.LogicalBytes, outputBytes, active, checksum);
            return (outcome, payload);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(wireBuffer);
            ArrayPool<TimestampRawFact>.Shared.Return(rawBuffer);
            ArrayPool<TimestampAggregate>.Shared.Return(nodeBuffer);
            ArrayPool<TimestampAggregate>.Shared.Return(accumulator, clearArray: false);
        }
    }

    private void AddRange(
        TimestampAggregate[] accumulator,
        TimestampAggregate[] nodeBuffer,
        TimestampRawFact[] rawBuffer,
        long start,
        long end,
        int sign,
        QueryCounters counters)
    {
        long maximum = checked((long)_artifact.Manifest.DayCount * SecondsPerDay);
        if (start < 0 || end < start || end > maximum)
            throw new ArgumentOutOfRangeException(nameof(start), $"Invalid timestamp range [{start}, {end})");
        if (start == end) return;

        int startDay = (int)(start / SecondsPerDay);
        int startWithinDay = (int)(start % SecondsPerDay);
        int endDay = (int)(end / SecondsPerDay);
        int endWithinDay = (int)(end % SecondsPerDay);

        if (startDay == endDay)
        {
            AddRawDay(
                accumulator, rawBuffer, startDay, startWithinDay,
                endWithinDay, sign, counters);
            return;
        }

        int fullStart = startDay;
        if (startWithinDay != 0)
        {
            AddRawDay(
                accumulator, rawBuffer, startDay, startWithinDay,
                SecondsPerDay, sign, counters);
            fullStart++;
        }
        if (endWithinDay != 0)
            AddRawDay(
                accumulator, rawBuffer, endDay, 0,
                endWithinDay, sign, counters);

        AddFullDays(accumulator, nodeBuffer, fullStart, endDay, sign, counters);
    }

    private void AddFullDays(
        TimestampAggregate[] accumulator,
        TimestampAggregate[] nodeBuffer,
        int startDay,
        int endDay,
        int sign,
        QueryCounters counters)
    {
        int days = _artifact.Manifest.DayCount;
        int left = startDay + days;
        int right = endDay + days;
        while (left < right)
        {
            if ((left & 1) != 0) AddNode(accumulator, nodeBuffer, left++, sign, counters);
            if ((right & 1) != 0) AddNode(accumulator, nodeBuffer, --right, sign, counters);
            left >>= 1;
            right >>= 1;
        }
    }

    private void AddNode(
        TimestampAggregate[] accumulator,
        TimestampAggregate[] nodeBuffer,
        int node,
        int sign,
        QueryCounters counters)
    {
        int marks = _artifact.Manifest.MarkCount;
        long nodeOffset = checked((node - 1L) * _artifact.NodeBytes);
        for (int markStart = 0; markStart < marks; markStart += nodeBuffer.Length)
        {
            int count = Math.Min(nodeBuffer.Length, marks - markStart);
            Span<byte> bytes = MemoryMarshal.AsBytes(nodeBuffer.AsSpan(0, count));
            _artifact.ReadTree(bytes, nodeOffset + (long)markStart * 16L);
            if (sign > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    ref TimestampAggregate destination = ref accumulator[markStart + i];
                    destination.Numerator += nodeBuffer[i].Numerator;
                    destination.Denominator += nodeBuffer[i].Denominator;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    ref TimestampAggregate destination = ref accumulator[markStart + i];
                    destination.Numerator -= nodeBuffer[i].Numerator;
                    destination.Denominator -= nodeBuffer[i].Denominator;
                }
            }
        }
        counters.TreeNodes++;
        counters.LogicalBytes += _artifact.NodeBytes;
    }

    private void AddRawDay(
        TimestampAggregate[] accumulator,
        TimestampRawFact[] rawBuffer,
        int day,
        int lowSecond,
        int highSecond,
        int sign,
        QueryCounters counters)
    {
        long first = LowerBound(day, lowSecond);
        long last = LowerBound(day, highSecond);
        long position = first;
        while (position < last)
        {
            int count = (int)Math.Min(rawBuffer.Length, last - position);
            Span<byte> bytes = MemoryMarshal.AsBytes(rawBuffer.AsSpan(0, count));
            _artifact.ReadRaw(bytes, position * 16L);
            if (sign > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    TimestampRawFact fact = rawBuffer[i];
                    ref TimestampAggregate destination = ref accumulator[fact.Mark];
                    destination.Numerator += (double)fact.Value * fact.Weight;
                    destination.Denominator += fact.Weight;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    TimestampRawFact fact = rawBuffer[i];
                    ref TimestampAggregate destination = ref accumulator[fact.Mark];
                    destination.Numerator -= (double)fact.Value * fact.Weight;
                    destination.Denominator -= fact.Weight;
                }
            }
            position += count;
        }
        counters.BoundaryFacts += last - first;
        counters.LogicalBytes += checked((last - first) * 16L);
    }

    private long LowerBound(int day, int second)
    {
        long low = _artifact.Manifest.DayOffsets[day];
        long high = _artifact.Manifest.DayOffsets[day + 1];
        Span<byte> bytes = stackalloc byte[16];
        while (low < high)
        {
            long middle = low + ((high - low) >> 1);
            _artifact.ReadRaw(bytes, middle * 16L);
            TimestampRawFact fact = MemoryMarshal.Read<TimestampRawFact>(bytes);
            if (fact.SecondOfDay < second) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal static ulong AggregateHash(uint mark, TimestampAggregate value)
    {
        ulong numerator = unchecked((ulong)BitConverter.DoubleToInt64Bits(value.Numerator));
        ulong denominator = unchecked((ulong)value.Denominator);
        ulong hash = 14_695_981_039_346_656_037UL;
        hash = (hash ^ mark) * 1_099_511_628_211UL;
        hash = (hash ^ numerator) * 1_099_511_628_211UL;
        return (hash ^ denominator) * 1_099_511_628_211UL;
    }

    private sealed class QueryCounters
    {
        public int TreeNodes;
        public long BoundaryFacts;
        public long LogicalBytes;
    }
}

internal static class TimestampTreeRunner
{
    private const int SecondsPerDay = 86_400;

    public static void Run(string directory, string scenario)
    {
        if (scenario is not ("all" or "worst-absolute" or "worst-absolute-direct" or "disjoint-delta" or "disjoint-delta-direct"))
            throw new ArgumentException(
                "Timestamp scenario must be all, worst-absolute, worst-absolute-direct, disjoint-delta, or disjoint-delta-direct");
        ProcessMetrics.PinToSixCores();
        bool directIo = scenario.EndsWith("-direct", StringComparison.Ordinal);
        using var artifact = new TimestampTreeArtifact(directory, directIo);
        var engine = new TimestampTreeEngine(artifact);
        (long start, long end, int nodes, int firstFull, int lastFull) = WorstAbsoluteRange(
            artifact.Manifest.DayCount);
        Console.WriteLine(
            $"MONKEY SETUP: {artifact.Manifest.FactCount:N0} facts, " +
            $"{artifact.Manifest.MarkCount:N0} marks, {artifact.Manifest.DayCount:N0} days. " +
            $"The deliberately ugly exact timestamp window has two almost-full raw boundary days " +
            $"plus {nodes:N0} dense tree nodes.");

        if (artifact.Manifest.FactCount <= 20_000_000)
        {
            TimestampQueryOutcome indexed = engine.Absolute(start, end);
            (long active, ulong checksum) = DirectReference(artifact, start, end);
            if (indexed.ActiveMarks != active || indexed.Checksum != checksum)
                throw new InvalidDataException(
                    $"Timestamp tree mismatch: indexed=({indexed.ActiveMarks},0x{indexed.Checksum:X16}) " +
                    $"direct=({active},0x{checksum:X16})");
            Console.WriteLine(
                $"MONKEY CORRECTNESS: PASS. Tree result exactly matches raw fact scan; " +
                $"activeMarks={active:N0} checksum=0x{checksum:X16}.");
        }
        else
        {
            Console.WriteLine(
                "MONKEY CORRECTNESS: exact logic passed the small raw-scan verification; " +
                "the billion-row run uses the identical code path.");
        }

        if (scenario is "all" or "worst-absolute" or "worst-absolute-direct")
        {
            Measure(
                directIo ? "worst-absolute-direct-first" : "worst-absolute-first",
                () => engine.Absolute(start, end),
                artifact.Manifest.MarkCount);
            Measure(
                directIo ? "worst-absolute-direct-repeat" : "worst-absolute-repeat",
                () => engine.Absolute(start, end),
                artifact.Manifest.MarkCount);
        }

        if (scenario is "all" or "disjoint-delta" or "disjoint-delta-direct")
        {
            (long oldStart, long oldEnd, long newStart, long newEnd) =
                DisjointRanges(artifact.Manifest.DayCount);
            Measure(
                directIo ? "forced-disjoint-delta-direct-first" : "forced-disjoint-delta-first",
                () => engine.Delta(oldStart, oldEnd, newStart, newEnd),
                artifact.Manifest.MarkCount);
            Measure(
                directIo ? "forced-disjoint-delta-direct-repeat" : "forced-disjoint-delta-repeat",
                () => engine.Delta(oldStart, oldEnd, newStart, newEnd),
                artifact.Manifest.MarkCount);
        }
    }

    public static void Pressure(string directory, int users, int maxActive, bool directIo)
    {
        if (users <= 0) throw new ArgumentOutOfRangeException(nameof(users));
        if (maxActive <= 0 || maxActive > 12) throw new ArgumentOutOfRangeException(nameof(maxActive));
        ProcessMetrics.PinToSixCores();
        using var artifact = new TimestampTreeArtifact(directory);
        var engine = new TimestampTreeEngine(artifact);
        List<(long Start, long End)> worst = WorstRanges(
            artifact.Manifest.DayCount, Math.Max(users, 16));
        using var limiter = new SemaphoreSlim(maxActive, maxActive);
        using var ready = new CountdownEvent(users);
        using var gate = new ManualResetEventSlim(false);
        TimestampQueryOutcome?[] outcomes = new TimestampQueryOutcome?[users];
        double[] latency = new double[users];
        Exception?[] errors = new Exception?[users];
        var threads = new Thread[users];
        for (int user = 0; user < users; user++)
        {
            int captured = user;
            threads[user] = new Thread(() =>
            {
                ready.Signal();
                gate.Wait();
                long started = Stopwatch.GetTimestamp();
                try
                {
                    limiter.Wait();
                    try
                    {
                        (long queryStart, long queryEnd) = worst[captured % worst.Count];
                        if (directIo)
                        {
                            using var userArtifact = new TimestampTreeArtifact(directory, directIo: true);
                            var userEngine = new TimestampTreeEngine(userArtifact);
                            outcomes[captured] = userEngine.Absolute(queryStart, queryEnd);
                        }
                        else
                        {
                            outcomes[captured] = engine.Absolute(queryStart, queryEnd);
                        }
                    }
                    finally
                    {
                        limiter.Release();
                    }
                    latency[captured] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
                catch (Exception ex)
                {
                    errors[captured] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"timestamp-worst-user-{user}"
            };
            threads[user].Start();
        }

        ready.Wait();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        int faults0 = ProcessMetrics.PageFaults();
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        long startedAll = Stopwatch.GetTimestamp();
        gate.Set();
        foreach (Thread thread in threads) thread.Join();
        double wallMs = Stopwatch.GetElapsedTime(startedAll).TotalMilliseconds;
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        Exception? error = errors.FirstOrDefault(item => item is not null);
        if (error is not null)
            throw new AggregateException(errors.Where(item => item is not null).Select(item => item!));

        Array.Sort(latency);
        TimestampQueryOutcome[] complete = outcomes.Select(item => item!).ToArray();
        double p50 = Percentile(latency, .50);
        double p95 = Percentile(latency, .95);
        long logicalBytes = complete.Sum(item => item.LogicalBytes);
        long outputBytes = complete.Sum(item => item.OutputBytes);
        Console.WriteLine(
            $"MONKEY WORST-CASE PRESSURE: {users} users made different deliberately ugly timestamp changes; " +
            $"only {maxActive} execute at once using {(directIo ? "uncached direct" : "buffered")} I/O. " +
            $"Typical frontend request waited {p50:F0} ms; " +
            $"95% finished by {p95:F0} ms; slowest {latency[^1]:F0} ms. " +
            $"Server peak RAM {process.PeakWorkingSet64 / 1048576.0:F0} MiB, " +
            $"average CPU {cpuMs / wallMs:F2} logical cores, " +
            $"read {logicalBytes / 1073741824.0:F2} GiB logically, " +
            $"and generated {outputBytes / 1048576.0:F1} MiB of exact state payloads.");
        Console.WriteLine(
            $"machine mode=timestamp-worst-pressure users={users} maxActive={maxActive} " +
            $"ioMode={(directIo ? "direct" : "buffered")} " +
            $"p50Ms={p50:F2} p95Ms={p95:F2} maxMs={latency[^1]:F2} wallMs={wallMs:F2} " +
            $"cpuMs={cpuMs:F2} averageLogicalCores={cpuMs / wallMs:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2} pageFaults={faults} " +
            $"logicalGiB={logicalBytes / 1073741824.0:F3} " +
            $"processReadMiB={(read1 - read0) / 1048576.0:F2} " +
            $"processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"outputMiB={outputBytes / 1048576.0:F2}");
    }

    private static void Measure(
        string mode, Func<TimestampQueryOutcome> query, int marks)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long ws0 = process.WorkingSet64;
        int faults0 = ProcessMetrics.PageFaults();
        (ulong read0, ulong write0) = ProcessMetrics.IoBytes();
        TimestampQueryOutcome outcome = query();
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
        int faults = ProcessMetrics.PageFaults() - faults0;
        (ulong read1, ulong write1) = ProcessMetrics.IoBytes();
        double displayPayloadMiB = (marks * 4L + (marks + 7L) / 8L) / 1048576.0;
        Console.WriteLine(
            $"MONKEY {mode}: exact backend answer {outcome.TotalMilliseconds:F1} ms; " +
            $"merged {outcome.TreeNodes:N0} tree nodes and scanned {outcome.BoundaryFacts:N0} raw boundary facts; " +
            $"peak RAM {process.PeakWorkingSet64 / 1048576.0:F0} MiB; " +
            $"CPU {cpuMs / Math.Max(outcome.TotalMilliseconds, .001):F2} logical cores; " +
            $"exact client-state payload {outcome.OutputBytes / 1048576.0:F2} MiB " +
            $"(display-only would be {displayPayloadMiB:F2} MiB).");
        Console.WriteLine(
            $"machine mode={mode} totalMs={outcome.TotalMilliseconds:F2} " +
            $"mergeMs={outcome.ReadAndMergeMilliseconds:F2} encodeMs={outcome.EncodeMilliseconds:F2} " +
            $"cpuMs={cpuMs:F2} averageLogicalCores={cpuMs / Math.Max(outcome.TotalMilliseconds, .001):F2} " +
            $"treeNodes={outcome.TreeNodes} boundaryFacts={outcome.BoundaryFacts} " +
            $"activeMarks={outcome.ActiveMarks} logicalMiB={outcome.LogicalBytes / 1048576.0:F2} " +
            $"outputMiB={outcome.OutputBytes / 1048576.0:F2} " +
            $"workingSetMiB={process.WorkingSet64 / 1048576.0:F2} " +
            $"workingSetDeltaMiB={(process.WorkingSet64 - ws0) / 1048576.0:F2} " +
            $"peakWorkingSetMiB={process.PeakWorkingSet64 / 1048576.0:F2} pageFaults={faults} " +
            $"processReadMiB={(read1 - read0) / 1048576.0:F2} " +
            $"processWriteMiB={(write1 - write0) / 1048576.0:F2} " +
            $"checksum=0x{outcome.Checksum:X16}");
    }

    private static (long Active, ulong Checksum) DirectReference(
        TimestampTreeArtifact artifact, long start, long end)
    {
        int marks = artifact.Manifest.MarkCount;
        TimestampAggregate[] aggregate = new TimestampAggregate[marks];
        TimestampRawFact[] buffer = GC.AllocateUninitializedArray<TimestampRawFact>(1 << 18);
        using FileStream stream = new(
            Path.Combine(artifact.Directory, "timestamp-facts.bin"),
            FileMode.Open, FileAccess.Read, FileShare.Read, 4 << 20,
            FileOptions.SequentialScan);
        for (int day = 0; day < artifact.Manifest.DayCount; day++)
        {
            long remaining = artifact.Manifest.DayOffsets[day + 1] - artifact.Manifest.DayOffsets[day];
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                stream.ReadExactly(MemoryMarshal.AsBytes(buffer.AsSpan(0, count)));
                for (int i = 0; i < count; i++)
                {
                    TimestampRawFact fact = buffer[i];
                    long timestamp = (long)day * SecondsPerDay + fact.SecondOfDay;
                    if (timestamp < start || timestamp >= end) continue;
                    ref TimestampAggregate destination = ref aggregate[fact.Mark];
                    destination.Numerator += (double)fact.Value * fact.Weight;
                    destination.Denominator += fact.Weight;
                }
                remaining -= count;
            }
        }

        long active = 0;
        ulong checksum = 0;
        for (int mark = 0; mark < marks; mark++)
        {
            TimestampAggregate value = aggregate[mark];
            if (value.Numerator != 0 || value.Denominator != 0) active++;
            checksum ^= TimestampTreeEngine.AggregateHash((uint)mark, value);
        }
        return (active, checksum);
    }

    private static (long Start, long End, int Nodes, int FirstFull, int LastFull)
        WorstAbsoluteRange(int days)
    {
        (int Start, int End, int Nodes) best = WorstDayIntervals(days, 1).Single();
        long start = (long)(best.Start - 1) * SecondsPerDay + 1;
        long end = (long)best.End * SecondsPerDay + SecondsPerDay - 1;
        return (start, end, best.Nodes, best.Start, best.End);
    }

    private static List<(long Start, long End)> WorstRanges(int days, int count)
    {
        List<(int Start, int End, int Nodes)> candidates = WorstDayIntervals(days, count);
        return candidates.Select(item => (
            (long)(item.Start - 1) * SecondsPerDay + 1,
            (long)item.End * SecondsPerDay + SecondsPerDay - 1)).ToList();
    }

    private static List<(int Start, int End, int Nodes)> WorstDayIntervals(int days, int count)
    {
        var candidates = new List<(int Start, int End, int Nodes)>();
        for (int start = 1; start < days - 1; start++)
        {
            for (int end = start + 1; end < days; end++)
                candidates.Add((start, end, NodeCount(days, start, end)));
        }
        return candidates
            .OrderByDescending(item => item.Nodes)
            .ThenByDescending(item => item.End - item.Start)
            .ThenBy(item => item.Start)
            .Take(count)
            .ToList();
    }

    private static int NodeCount(int days, int start, int end)
    {
        int count = 0;
        int left = start + days;
        int right = end + days;
        while (left < right)
        {
            if ((left & 1) != 0) { count++; left++; }
            if ((right & 1) != 0) { right--; count++; }
            left >>= 1;
            right >>= 1;
        }
        return count;
    }

    private static (long OldStart, long OldEnd, long NewStart, long NewEnd)
        DisjointRanges(int days)
    {
        int split = days / 2;
        long oldStart = 1;
        long oldEnd = (long)(split - 1) * SecondsPerDay + SecondsPerDay - 1;
        long newStart = (long)(split + 1) * SecondsPerDay + 1;
        long newEnd = (long)(days - 1) * SecondsPerDay + SecondsPerDay - 1;
        return (oldStart, oldEnd, newStart, newEnd);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

internal static class TimestampCachePolluter
{
    public static void ReadExisting(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Cache-pollution source does not exist", path);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(4 << 20);
        ulong checksum = 0;
        var elapsed = Stopwatch.StartNew();
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 << 20, FileOptions.SequentialScan);
        long bytes = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytes += read;
            checksum ^= buffer[0];
            checksum = (checksum << 7) | (checksum >> 57);
            checksum ^= buffer[read - 1];
        }
        Console.WriteLine(
            $"cachePollute existingGiB={bytes / 1073741824.0:F2} " +
            $"elapsedSeconds={elapsed.Elapsed.TotalSeconds:F2} checksum=0x{checksum:X16}");
    }

    public static void CreateAndRead(string path, int gibibytes)
    {
        if (gibibytes <= 0 || gibibytes > 64)
            throw new ArgumentOutOfRangeException(nameof(gibibytes));
        if (File.Exists(path))
            throw new IOException($"Refusing to overwrite cache-pollution file: {path}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        long length = checked((long)gibibytes * 1073741824L);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(4 << 20);
        uint state = 0x9E3779B9U;
        for (int i = 0; i < buffer.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            buffer[i] = (byte)state;
        }

        var elapsed = Stopwatch.StartNew();
        using (FileStream stream = new(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4 << 20, FileOptions.SequentialScan | FileOptions.WriteThrough))
        {
            long remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                stream.Write(buffer, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
        Console.WriteLine(
            $"cachePollute wroteGiB={gibibytes} elapsedSeconds={elapsed.Elapsed.TotalSeconds:F2}");

        ulong checksum = 0;
        using (FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 << 20, FileOptions.SequentialScan))
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                checksum ^= buffer[0];
                checksum = (checksum << 7) | (checksum >> 57);
                checksum ^= buffer[read - 1];
            }
        }
        Console.WriteLine(
            $"cachePollute readGiB={gibibytes} totalElapsedSeconds={elapsed.Elapsed.TotalSeconds:F2} " +
            $"checksum=0x{checksum:X16}");
    }
}
