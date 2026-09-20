using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using DuckDB.NET.Data;

namespace Colossus.NumericFilterBench;

internal static class ArtifactBuilder
{
    public static async Task BuildAsync(string parquet, string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException($"Refusing to overwrite non-empty artifact directory: {directory}");
        Directory.CreateDirectory(directory);

        var total = Stopwatch.StartNew();
        Console.WriteLine($"source={parquet}");
        Console.WriteLine($"artifact={directory}");

        await using var connection = new DuckDBConnection("DataSource=:memory:");
        await connection.OpenAsync();
        long factCount64 = await ScalarLongAsync(
            connection, "SELECT count(*) FROM read_parquet($path)", parquet);
        if (factCount64 > int.MaxValue)
            throw new NotSupportedException("The disposable benchmark uses UInt32 local row IDs per shard");
        int n = checked((int)factCount64);

        uint[] mark = GC.AllocateUninitializedArray<uint>(n);
        float[] tests = GC.AllocateUninitializedArray<float>(n);
        float[][] values = Enumerable.Range(0, Fields.Names.Length)
            .Select(_ => GC.AllocateUninitializedArray<float>(n)).ToArray();
        Dictionary<ulong, uint>? markIds = new(2_100_000);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT x, y, tests, sample_share, download_mbps, upload_mbps, latency_ms
                FROM read_parquet($path)
                """;
            command.Parameters.Add(new DuckDBParameter("path", parquet));
            await using var reader = await command.ExecuteReaderAsync();
            int row = 0;
            while (await reader.ReadAsync())
            {
                float x = reader.GetFloat(0);
                float y = reader.GetFloat(1);
                ulong geometry = ((ulong)unchecked((uint)BitConverter.SingleToInt32Bits(x)) << 32) |
                    unchecked((uint)BitConverter.SingleToInt32Bits(y));
                if (!markIds.TryGetValue(geometry, out uint markId))
                {
                    markId = checked((uint)markIds.Count);
                    markIds.Add(geometry, markId);
                }

                mark[row] = markId;
                tests[row] = RequiredFloat(reader, 2, "tests", row);
                for (int d = 0; d < Fields.Names.Length; d++)
                    values[d][row] = RequiredFloat(reader, d + 3, Fields.Names[d], row);
                row++;
                if (row % 5_000_000 == 0)
                    Console.WriteLine($"loadedRows={row:N0} elapsedSeconds={total.Elapsed.TotalSeconds:F2}");
            }
            if (row != n)
                throw new InvalidDataException($"Expected {n:N0} rows but read {row:N0}");
        }

        int marks = markIds.Count;
        Console.WriteLine($"loadedFacts={n:N0} marks={marks:N0}");
        markIds = null;

        int[] counts = new int[marks];
        for (int row = 0; row < n; row++)
            counts[mark[row]]++;
        int maxFactsPerMark = counts.Max();

        uint[] offsets = new uint[marks + 1];
        for (int m = 0; m < marks; m++)
            offsets[m + 1] = checked(offsets[m] + (uint)counts[m]);
        int[] cursor = new int[marks];
        for (int m = 0; m < marks; m++)
            cursor[m] = checked((int)offsets[m]);

        uint[] groupedMark = GC.AllocateUninitializedArray<uint>(n);
        float[] groupedTests = GC.AllocateUninitializedArray<float>(n);
        float[][] groupedValues = Enumerable.Range(0, Fields.Names.Length)
            .Select(_ => GC.AllocateUninitializedArray<float>(n)).ToArray();
        for (int row = 0; row < n; row++)
        {
            uint markId = mark[row];
            int dest = cursor[markId]++;
            groupedMark[dest] = markId;
            groupedTests[dest] = tests[row];
            for (int d = 0; d < Fields.Names.Length; d++)
                groupedValues[d][dest] = values[d][row];
        }
        mark = groupedMark;
        tests = groupedTests;
        values = groupedValues;
        cursor = null!;
        counts = null!;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Console.WriteLine($"groupedSeconds={total.Elapsed.TotalSeconds:F2} workingSetMiB={Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F2}");

        WritePlane(Path.Combine(directory, "fact-mark.u32"), mark);
        WritePlane(Path.Combine(directory, "fact-tests.f32"), tests);
        for (int d = 0; d < Fields.Names.Length; d++)
            WritePlane(Path.Combine(directory, $"fact-{Fields.Names[d]}.f32"), values[d]);
        WritePlane(Path.Combine(directory, "mark-offsets.u32"), offsets);

        double[] baseNumerator = new double[marks];
        double[] baseDenominator = new double[marks];
        float[][] mins = Enumerable.Range(0, Fields.Names.Length).Select(_ => new float[marks]).ToArray();
        float[][] maxs = Enumerable.Range(0, Fields.Names.Length).Select(_ => new float[marks]).ToArray();
        for (int m = 0; m < marks; m++)
        {
            int start = checked((int)offsets[m]);
            int end = checked((int)offsets[m + 1]);
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                mins[d][m] = float.PositiveInfinity;
                maxs[d][m] = float.NegativeInfinity;
            }
            double numerator = 0;
            double denominator = 0;
            for (int row = start; row < end; row++)
            {
                float weight = tests[row];
                numerator += (double)(values[Fields.Download][row] * weight);
                denominator += weight;
                for (int d = 0; d < Fields.Names.Length; d++)
                {
                    float v = values[d][row];
                    if (v < mins[d][m]) mins[d][m] = v;
                    if (v > maxs[d][m]) maxs[d][m] = v;
                }
            }
            baseNumerator[m] = numerator;
            baseDenominator[m] = denominator;
        }
        WritePlane(Path.Combine(directory, "mark-base-numerator.f64"), baseNumerator);
        WritePlane(Path.Combine(directory, "mark-base-denominator.f64"), baseDenominator);
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            WritePlane(Path.Combine(directory, $"mark-{Fields.Names[d]}-min.f32"), mins[d]);
            WritePlane(Path.Combine(directory, $"mark-{Fields.Names[d]}-max.f32"), maxs[d]);
        }

        var quantiles = new QuantileSet[Fields.Names.Length];
        ulong[] order = GC.AllocateUninitializedArray<ulong>(n);
        ulong[] scratch = GC.AllocateUninitializedArray<ulong>(n);
        uint[] coverPlane = GC.AllocateUninitializedArray<uint>(n);
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            Console.WriteLine($"sorting={Fields.Names[d]}");
            for (int row = 0; row < n; row++)
                order[row] = ((ulong)FloatOrder.Sortable(values[d][row]) << 32) | unchecked((uint)row);
            RadixSortByHighUInt32(order, scratch);
            WritePlane(Path.Combine(directory, $"index-{Fields.Names[d]}.u64"), order);

            quantiles[d] = new QuantileSet(
                Quantile(order, .01), Quantile(order, .05), Quantile(order, .10),
                Quantile(order, .25), Quantile(order, .45), Quantile(order, .55),
                Quantile(order, .75), Quantile(order, .90), Quantile(order, .95), Quantile(order, .99));

            for (int i = 0; i < n; i++) coverPlane[i] = unchecked((uint)order[i]);
            WritePlane(Path.Combine(directory, $"cover-{Fields.Names[d]}-row.u32"), coverPlane);
            ReorderAndWrite(directory, Fields.Names[d], "mark.u32", order, mark, coverPlane);
            ReorderAndWrite(directory, Fields.Names[d], "tests.f32", order, tests, coverPlane);
            for (int source = 0; source < Fields.Names.Length; source++)
                ReorderAndWrite(directory, Fields.Names[d], $"{Fields.Names[source]}.f32", order, values[source], coverPlane);
            Console.WriteLine($"sorted={Fields.Names[d]} elapsedSeconds={total.Elapsed.TotalSeconds:F2}");
        }

        long bytes = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
        var manifest = new ArtifactManifest(
            1, n, marks, maxFactsPerMark, Fields.Names, quantiles, [], total.Elapsed.TotalSeconds, bytes);
        RangeBox[] referenceCases =
        [
            .. BenchmarkRunner.Cases(manifest),
            RangeBox.Quantile(manifest, 5, 95).WithLower(Fields.Download, 10f, "download-10.00"),
            RangeBox.Quantile(manifest, 5, 95).WithLower(Fields.Download, 10.25f, "download-10.25")
        ];
        var expected = new Dictionary<string, ExpectedResult>(StringComparer.Ordinal);
        foreach (RangeBox box in referenceCases)
        {
            expected.Add(box.Name, Reference(box, offsets, tests, values));
            Console.WriteLine($"reference={box.Name} rows={expected[box.Name].Rows} checksum=0x{expected[box.Name].Checksum:X16}");
        }

        total.Stop();
        manifest = manifest with { Expected = expected, BuildSeconds = total.Elapsed.TotalSeconds };
        manifest.Write(directory);
        bytes = Directory.EnumerateFiles(directory).Sum(path => new FileInfo(path).Length);
        manifest = manifest with { ArtifactBytes = bytes };
        manifest.Write(directory);
        Console.WriteLine($"buildSeconds={total.Elapsed.TotalSeconds:F2}");
        Console.WriteLine($"artifactBytes={bytes}");
        Console.WriteLine($"artifactGiB={bytes / 1073741824.0:F3}");
        Console.WriteLine($"maxFactsPerMark={maxFactsPerMark}");
        Console.WriteLine($"peakWorkingSetMiB={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F2}");
    }

    private static float RequiredFloat(System.Data.Common.DbDataReader reader, int ordinal, string name, int row)
    {
        if (reader.IsDBNull(ordinal))
            throw new InvalidDataException($"Null {name} at row {row:N0}");
        float value = reader.GetFloat(ordinal);
        if (!float.IsFinite(value))
            throw new InvalidDataException($"Non-finite {name} at row {row:N0}");
        return value;
    }

    private static async Task<long> ScalarLongAsync(DuckDBConnection connection, string sql, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new DuckDBParameter("path", path));
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void RadixSortByHighUInt32(ulong[] data, ulong[] scratch)
    {
        int[] counts = new int[1 << 16];
        RadixPass(data, scratch, counts, 32);
        Array.Clear(counts);
        RadixPass(scratch, data, counts, 48);
    }

    private static void RadixPass(ulong[] source, ulong[] destination, int[] counts, int shift)
    {
        foreach (ulong item in source)
            counts[(int)((item >> shift) & 0xFFFF)]++;
        int sum = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            int count = counts[i];
            counts[i] = sum;
            sum += count;
        }
        foreach (ulong item in source)
            destination[counts[(int)((item >> shift) & 0xFFFF)]++] = item;
    }

    private static float Quantile(ulong[] order, double q)
    {
        int index = Math.Clamp((int)Math.Round((order.Length - 1) * q, MidpointRounding.ToEven), 0, order.Length - 1);
        return FloatOrder.FromSortable(unchecked((uint)(order[index] >> 32)));
    }

    private static ExpectedResult Reference(
        RangeBox box, uint[] offsets, float[] tests, float[][] values)
    {
        long visible = 0;
        ulong checksum = 0;
        for (int mark = 0; mark < offsets.Length - 1; mark++)
        {
            double numerator = 0;
            double denominator = 0;
            int start = checked((int)offsets[mark]);
            int end = checked((int)offsets[mark + 1]);
            for (int row = start; row < end; row++)
            {
                bool pass = true;
                for (int d = 0; d < Fields.Names.Length; d++)
                {
                    float value = values[d][row];
                    if (value < box.Low[d] || value > box.High[d])
                    {
                        pass = false;
                        break;
                    }
                }
                if (!pass) continue;
                float weight = tests[row];
                numerator += (double)(values[Fields.Download][row] * weight);
                denominator += weight;
            }
            if (!(denominator > 0)) continue;
            checksum ^= BenchmarkHash.MarkValue((uint)mark, (float)(numerator / denominator));
            visible++;
        }
        return new ExpectedResult(visible, checksum);
    }

    private static void ReorderAndWrite<T>(
        string directory, string dimension, string suffix, ulong[] order, T[] source, uint[] bits)
        where T : unmanaged
    {
        if (Marshal.SizeOf<T>() != sizeof(uint))
            throw new NotSupportedException("Covering planes are four-byte primitives");
        Span<T> destination = MemoryMarshal.Cast<uint, T>(bits.AsSpan());
        for (int i = 0; i < order.Length; i++)
            destination[i] = source[checked((int)unchecked((uint)order[i]))];
        WritePlane(Path.Combine(directory, $"cover-{dimension}-{suffix}"), destination);
    }

    private static void WritePlane<T>(string path, T[] values) where T : unmanaged =>
        WritePlane(path, values.AsSpan());

    private static void WritePlane<T>(string path, Span<T> values) where T : unmanaged
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        stream.Write(MemoryMarshal.AsBytes(values));
    }
}
