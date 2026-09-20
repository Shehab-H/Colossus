using System.Diagnostics;
using System.Buffers.Binary;
using System.Numerics;

namespace Colossus.NumericFilterBench;

internal static class BenchmarkHash
{
    public static ulong MarkValue(uint mark, float value)
    {
        ulong z = ((ulong)mark << 32) | unchecked((uint)BitConverter.SingleToInt32Bits(value));
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

internal sealed record QueryOutcome(
    string Engine,
    string Query,
    double WallMilliseconds,
    double CpuMilliseconds,
    long Rows,
    ulong Checksum,
    long RecordsVisited,
    long UniqueFacts,
    long LogicalBytes,
    long WorkingSetBytes,
    long WorkingSetDeltaBytes,
    long PeakWorkingSetBytes,
    int PageFaults,
    ulong ProcessReadBytes,
    ulong ProcessWriteBytes);

internal sealed record StatelessDeltaOutcome(
    double TotalMilliseconds,
    double BoundaryMilliseconds,
    double RecomputeMilliseconds,
    double EncodeMilliseconds,
    ExpectedResult Changed,
    long BoundaryFacts,
    long RecomputedFacts,
    long LogicalBytes,
    byte[] Buffer,
    int Length);

internal sealed class StatelessWorkspace
{
    private readonly ulong[] _candidateBits;
    private uint[] _marks;
    private float[] _values;
    private byte[] _output = [];

    public StatelessWorkspace(int marks)
    {
        _candidateBits = new ulong[(marks + 63) / 64];
        int initial = Math.Min(marks, 65_536);
        _marks = new uint[initial];
        _values = new float[initial];
    }

    public int Count { get; private set; }
    public byte[] Output => _output;

    public void BeginCandidates()
    {
        Array.Clear(_candidateBits);
        Count = 0;
    }

    public void MarkCandidate(uint mark) =>
        _candidateBits[(int)(mark >> 6)] |= 1UL << ((int)mark & 63);

    public int MaterializeCandidates()
    {
        int count = 0;
        foreach (ulong word in _candidateBits)
            count += BitOperations.PopCount(word);
        EnsureCandidates(count);

        int output = 0;
        for (int wordIndex = 0; wordIndex < _candidateBits.Length; wordIndex++)
        {
            ulong word = _candidateBits[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                _marks[output++] = (uint)((wordIndex << 6) + bit);
                word &= word - 1;
            }
        }
        Count = count;
        return count;
    }

    public uint Mark(int index) => _marks[index];
    public float Value(int index) => _values[index];
    public void SetValue(int index, float value) => _values[index] = value;

    public (byte[] Buffer, int Length) Encode(uint revision)
    {
        int required = checked(8 + Count * 9);
        if (_output.Length < required)
            _output = GC.AllocateUninitializedArray<byte>(RoundCapacity(required));

        BinaryPrimitives.WriteUInt32LittleEndian(_output.AsSpan(0, 4), revision);
        BinaryPrimitives.WriteUInt32LittleEndian(_output.AsSpan(4, 4), (uint)Count);
        int offset = 8;
        uint previous = 0;
        for (int i = 0; i < Count; i++)
        {
            uint mark = _marks[i];
            uint delta = mark - previous;
            previous = mark;
            while (delta >= 0x80)
            {
                _output[offset++] = (byte)(delta | 0x80);
                delta >>= 7;
            }
            _output[offset++] = (byte)delta;
            BinaryPrimitives.WriteSingleLittleEndian(_output.AsSpan(offset, 4), _values[i]);
            offset += 4;
        }
        return (_output, offset);
    }

    private void EnsureCandidates(int count)
    {
        if (_marks.Length >= count) return;
        int capacity = RoundCapacity(count);
        Array.Resize(ref _marks, capacity);
        Array.Resize(ref _values, capacity);
    }

    private static int RoundCapacity(int required)
    {
        int capacity = 1;
        while (capacity < required)
            capacity = checked(capacity << 1);
        return capacity;
    }
}

internal sealed class QueryWorkspace
{
    private ulong[]? _seenFacts;
    private readonly ulong[] _changedMarks;

    public QueryWorkspace(int marks)
    {
        Numerator = new double[marks];
        Denominator = new double[marks];
        _changedMarks = new ulong[(marks + 63) / 64];
    }

    public double[] Numerator { get; }
    public double[] Denominator { get; }
    public bool Complement { get; private set; }

    public void Clear(bool complement = false)
    {
        Array.Clear(Numerator);
        Array.Clear(Denominator);
        Complement = complement;
    }

    public void Add(uint mark, double numerator, double denominator)
    {
        Numerator[mark] += numerator;
        Denominator[mark] += denominator;
    }

    public void EnsureFactBitmap(int facts)
    {
        int words = checked((facts + 63) / 64);
        _seenFacts ??= new ulong[words];
        Array.Clear(_seenFacts);
    }

    public ulong[] FactBitmap => _seenFacts ?? throw new InvalidOperationException("Fact bitmap is not initialized");

    public void MarkFact(uint row) => _seenFacts![(int)(row >> 6)] |= 1UL << ((int)row & 63);

    public bool FirstFact(uint row)
    {
        ulong bit = 1UL << ((int)row & 63);
        ref ulong word = ref _seenFacts![(int)(row >> 6)];
        if ((word & bit) != 0) return false;
        word |= bit;
        return true;
    }

    public void BeginChanged() => Array.Clear(_changedMarks);

    public void MarkChanged(uint mark) => _changedMarks[(int)(mark >> 6)] |= 1UL << ((int)mark & 63);

    public ExpectedResult CompleteChanged()
    {
        long rows = 0;
        ulong checksum = 0;
        for (int wordIndex = 0; wordIndex < _changedMarks.Length; wordIndex++)
        {
            ulong word = _changedMarks[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int mark = (wordIndex << 6) + bit;
                double denominator = Denominator[mark];
                float value = denominator > 0 ? (float)(Numerator[mark] / denominator) : float.NaN;
                checksum ^= BenchmarkHash.MarkValue((uint)mark, value);
                rows++;
                word &= word - 1;
            }
        }
        return new ExpectedResult(rows, checksum);
    }

    public byte[] EncodeChanged(uint revision)
    {
        int count = 0;
        foreach (ulong word in _changedMarks) count += BitOperations.PopCount(word);
        byte[] payload = GC.AllocateUninitializedArray<byte>(checked(8 + count * 8));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), revision);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)count);
        int offset = 8;
        for (int wordIndex = 0; wordIndex < _changedMarks.Length; wordIndex++)
        {
            ulong word = _changedMarks[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int mark = (wordIndex << 6) + bit;
                double denominator = Denominator[mark];
                float value = denominator > 0 ? (float)(Numerator[mark] / denominator) : float.NaN;
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), (uint)mark);
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 4, 4), value);
                offset += 8;
                word &= word - 1;
            }
        }
        return payload;
    }

    public (byte[] Buffer, int Length) EncodeChangedVarint(uint revision)
    {
        int count = 0;
        foreach (ulong word in _changedMarks) count += BitOperations.PopCount(word);
        byte[] payload = GC.AllocateUninitializedArray<byte>(checked(8 + count * 9));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), revision);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)count);
        int offset = 8;
        uint previous = 0;
        for (int wordIndex = 0; wordIndex < _changedMarks.Length; wordIndex++)
        {
            ulong word = _changedMarks[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                uint mark = (uint)((wordIndex << 6) + bit);
                uint delta = mark - previous;
                previous = mark;
                while (delta >= 0x80)
                {
                    payload[offset++] = (byte)(delta | 0x80);
                    delta >>= 7;
                }
                payload[offset++] = (byte)delta;
                double denominator = Denominator[mark];
                float value = denominator > 0 ? (float)(Numerator[mark] / denominator) : float.NaN;
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, 4), value);
                offset += 4;
                word &= word - 1;
            }
        }
        return (payload, offset);
    }

    public ExpectedResult Complete(MappedArtifact artifact)
    {
        long rows = 0;
        ulong checksum = 0;
        for (int mark = 0; mark < Numerator.Length; mark++)
        {
            double denominator = Complement
                ? artifact.BaseDenominator[mark] - Denominator[mark]
                : Denominator[mark];
            if (!(denominator > 0)) continue;
            double numerator = Complement
                ? artifact.BaseNumerator[mark] - Numerator[mark]
                : Numerator[mark];
            float value = (float)(numerator / denominator);
            checksum ^= BenchmarkHash.MarkValue((uint)mark, value);
            rows++;
        }
        return new ExpectedResult(rows, checksum);
    }

    public QueryWorkspace CloneDirect()
    {
        if (Complement)
            throw new InvalidOperationException("Materialize complement state before cloning");
        var clone = new QueryWorkspace(Numerator.Length);
        Numerator.CopyTo(clone.Numerator, 0);
        Denominator.CopyTo(clone.Denominator, 0);
        return clone;
    }
}

internal unsafe sealed class ExactQueryEngine
{
    private readonly MappedArtifact _artifact;

    public ExactQueryEngine(MappedArtifact artifact) => _artifact = artifact;

    public QueryOutcome Scan(RangeBox box, QueryWorkspace workspace) =>
        Measure("scan", box, () => ScanCore(box, workspace));

    public QueryOutcome MarkPruned(RangeBox box, QueryWorkspace workspace) =>
        Measure("mark", box, () => MarkCore(box, workspace));

    public QueryOutcome SharedIndex(RangeBox box, QueryWorkspace workspace) =>
        Measure("index", box, () => IndexCore(box, workspace, covering: false));

    public QueryOutcome BitmapIndex(RangeBox box, QueryWorkspace workspace) =>
        Measure("bitmap", box, () => BitmapIndexCore(box, workspace));

    public QueryOutcome CoveringIndex(RangeBox box, QueryWorkspace workspace) =>
        Measure("cover", box, () => IndexCore(box, workspace, covering: true));

    public (double Milliseconds, ExpectedResult Result, long Visited, long LogicalBytes) CoveringFast(
        RangeBox box, QueryWorkspace workspace)
    {
        var sw = Stopwatch.StartNew();
        var result = IndexCore(box, workspace, covering: true);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, result.Result, result.Visited, result.Bytes);
    }

    public (double Milliseconds, ExpectedResult Result, long Visited, long LogicalBytes) BitmapFast(
        RangeBox box, QueryWorkspace workspace)
    {
        var sw = Stopwatch.StartNew();
        var result = BitmapIndexCore(box, workspace);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, result.Result, result.Visited, result.Bytes);
    }

    public QueryOutcome Delta(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state, bool covering = true) =>
        Measure(covering ? "cover-delta" : "index-delta", newBox,
            () => DeltaCore(oldBox, newBox, dimension, state, covering));

    public QueryOutcome BitmapDelta(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state) =>
        Measure("bitmap-delta", newBox,
            () => BitmapDeltaCore(oldBox, newBox, dimension, state));

    public (double Milliseconds, ExpectedResult Changed) DeltaFast(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state, bool covering = true)
    {
        var sw = Stopwatch.StartNew();
        var result = DeltaCore(oldBox, newBox, dimension, state, covering);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, result.Result);
    }

    public (double Milliseconds, ExpectedResult Changed) BitmapDeltaFast(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state)
    {
        var sw = Stopwatch.StartNew();
        var result = BitmapDeltaCore(oldBox, newBox, dimension, state);
        sw.Stop();
        return (sw.Elapsed.TotalMilliseconds, result.Result);
    }

    public StatelessDeltaOutcome StatelessDeltaFast(
        RangeBox oldBox, RangeBox newBox, int dimension,
        StatelessWorkspace workspace, uint revision = 0, bool coveringBoundary = true)
    {
        if (!SameExceptBoundary(oldBox, newBox, dimension))
            throw new ArgumentException("Stateless delta moves one lower boundary only");

        long totalStarted = Stopwatch.GetTimestamp();
        long boundaryStarted = Stopwatch.GetTimestamp();
        workspace.BeginCandidates();

        float low = Math.Min(oldBox.Low[dimension], newBox.Low[dimension]);
        float high = Math.Max(oldBox.Low[dimension], newBox.Low[dimension]);
        int start;
        int end;
        if (coveringBoundary)
        {
            CoverProjection projection = _artifact.Cover[dimension];
            start = LowerBoundCover(projection, dimension, low, strict: false);
            end = LowerBoundCover(projection, dimension, high, strict: false);
            for (int position = start; position < end; position++)
            {
                if (PassOtherCover(projection, position, oldBox, dimension))
                    workspace.MarkCandidate(projection.Mark[position]);
            }
        }
        else
        {
            MappedArray<ulong> order = _artifact.Index[dimension];
            start = LowerBoundIndex(order, low, strict: false);
            end = LowerBoundIndex(order, high, strict: false);
            for (int position = start; position < end; position++)
            {
                uint row = unchecked((uint)order[position]);
                if (PassOtherFact((int)row, oldBox, dimension))
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
                if (!PassFact(row, newBox)) continue;
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
        (byte[] buffer, int length) = workspace.Encode(revision);
        double encodeMs = Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds;
        double totalMs = Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds;
        long boundaryFacts = end - start;
        long logicalBytes = boundaryFacts * (coveringBoundary ? 16L : 24L) +
            recomputedFacts * 20L + candidates * 8L;
        return new StatelessDeltaOutcome(
            totalMs, boundaryMs, recomputeMs, encodeMs,
            new ExpectedResult(candidates, checksum),
            boundaryFacts, recomputedFacts, logicalBytes, buffer, length);
    }

    public void ScanState(RangeBox box, QueryWorkspace workspace) => ScanCore(box, workspace);

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) ScanCore(
        RangeBox box, QueryWorkspace workspace)
    {
        workspace.Clear();
        int n = checked((int)_artifact.Manifest.FactCount);
        for (int row = 0; row < n; row++)
        {
            if (!PassFact(row, box)) continue;
            float tests = _artifact.Tests[row];
            workspace.Add(_artifact.Mark[row],
                (double)(_artifact.Values[Fields.Download][row] * tests), tests);
        }
        return (workspace.Complete(_artifact), n, 0, (long)n * 24);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) MarkCore(
        RangeBox box, QueryWorkspace workspace)
    {
        workspace.Clear();
        long partialFacts = 0;
        long containedMarks = 0;
        int marks = _artifact.Manifest.MarkCount;
        for (int mark = 0; mark < marks; mark++)
        {
            bool disjoint = false;
            bool contained = true;
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                float min = _artifact.Min[d][mark];
                float max = _artifact.Max[d][mark];
                if (max < box.Low[d] || min > box.High[d])
                {
                    disjoint = true;
                    break;
                }
                if (min < box.Low[d] || max > box.High[d])
                    contained = false;
            }
            if (disjoint) continue;
            if (contained)
            {
                workspace.Add((uint)mark, _artifact.BaseNumerator[mark], _artifact.BaseDenominator[mark]);
                containedMarks++;
                continue;
            }

            int start = checked((int)_artifact.Offsets[mark]);
            int end = checked((int)_artifact.Offsets[mark + 1]);
            partialFacts += end - start;
            for (int row = start; row < end; row++)
            {
                if (!PassFact(row, box)) continue;
                float tests = _artifact.Tests[row];
                workspace.Add((uint)mark,
                    (double)(_artifact.Values[Fields.Download][row] * tests), tests);
            }
        }
        long bytes = (long)marks * 32 + containedMarks * 16 + partialFacts * 20;
        return (workspace.Complete(_artifact), partialFacts, 0, bytes);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) IndexCore(
        RangeBox box, QueryWorkspace workspace, bool covering)
    {
        var spans = new (int Low, int High)[Fields.Names.Length];
        int driver = 0;
        long inside = long.MaxValue;
        long outside = 0;
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            spans[d] = covering
                ? CoverSpan(_artifact.Cover[d], d, box.Low[d], box.High[d])
                : IndexSpan(_artifact.Index[d], box.Low[d], box.High[d]);
            long count = spans[d].High - spans[d].Low;
            if (count < inside)
            {
                inside = count;
                driver = d;
            }
            outside += _artifact.Manifest.FactCount - count;
        }

        long inclusionCost = inside * (covering ? 24 : 32);
        long complementCost = outside * (covering ? 16 : 20);
        if (complementCost < inclusionCost)
            return ComplementCore(box, workspace, covering, spans, outside);
        return InclusionCore(box, workspace, covering, spans[driver], driver, inside);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) BitmapIndexCore(
        RangeBox box, QueryWorkspace workspace)
    {
        var spans = new (int Low, int High)[Fields.Names.Length];
        int driver = 0;
        long inside = long.MaxValue;
        long outside = 0;
        int facts = checked((int)_artifact.Manifest.FactCount);
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            spans[d] = CoverSpan(_artifact.Cover[d], d, box.Low[d], box.High[d]);
            long count = spans[d].High - spans[d].Low;
            if (count < inside)
            {
                inside = count;
                driver = d;
            }
            outside += facts - count;
        }

        long inclusionCost = inside * 28;
        long complementCost = outside * 16;
        workspace.Clear(complement: complementCost < inclusionCost);
        workspace.EnsureFactBitmap(facts);
        long visited;
        if (!workspace.Complement)
        {
            CoverProjection projection = _artifact.Cover[driver];
            for (int position = spans[driver].Low; position < spans[driver].High; position++)
                workspace.MarkFact(projection.Row[position]);
            visited = inside;
        }
        else
        {
            visited = outside;
            for (int d = 0; d < Fields.Names.Length; d++)
            {
                CoverProjection projection = _artifact.Cover[d];
                for (int position = 0; position < spans[d].Low; position++)
                    workspace.MarkFact(projection.Row[position]);
                for (int position = spans[d].High; position < facts; position++)
                    workspace.MarkFact(projection.Row[position]);
            }
        }

        long unique = 0;
        ulong[] bitmap = workspace.FactBitmap;
        for (int wordIndex = 0; wordIndex < bitmap.Length; wordIndex++)
        {
            ulong word = bitmap[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int row = (wordIndex << 6) + bit;
                if (row >= facts) break;
                unique++;
                if (workspace.Complement || PassFact(row, box))
                {
                    float tests = _artifact.Tests[row];
                    workspace.Add(_artifact.Mark[row],
                        (double)(_artifact.Values[Fields.Download][row] * tests), tests);
                }
                word &= word - 1;
            }
        }
        long bytes = visited * 4 + unique * (workspace.Complement ? 12 : 24);
        return (workspace.Complete(_artifact), visited, unique, bytes);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) InclusionCore(
        RangeBox box, QueryWorkspace workspace, bool covering,
        (int Low, int High) span, int driver, long visited)
    {
        workspace.Clear();
        if (covering)
        {
            CoverProjection projection = _artifact.Cover[driver];
            for (int position = span.Low; position < span.High; position++)
            {
                if (!PassCover(projection, position, box)) continue;
                float tests = projection.Tests[position];
                workspace.Add(projection.Mark[position],
                    (double)(projection.Values[Fields.Download][position] * tests), tests);
            }
        }
        else
        {
            MappedArray<ulong> index = _artifact.Index[driver];
            for (int position = span.Low; position < span.High; position++)
            {
                uint row = unchecked((uint)index[position]);
                if (!PassFact((int)row, box)) continue;
                float tests = _artifact.Tests[(int)row];
                workspace.Add(_artifact.Mark[(int)row],
                    (double)(_artifact.Values[Fields.Download][(int)row] * tests), tests);
            }
        }
        long bytes = visited * (covering ? 24 : 32);
        return (workspace.Complete(_artifact), visited, 0, bytes);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) ComplementCore(
        RangeBox box, QueryWorkspace workspace, bool covering,
        (int Low, int High)[] spans, long visited)
    {
        workspace.Clear(complement: true);
        workspace.EnsureFactBitmap(checked((int)_artifact.Manifest.FactCount));
        long unique = 0;
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            if (covering)
            {
                CoverProjection projection = _artifact.Cover[d];
                ConsumeCoverTail(projection, 0, spans[d].Low);
                ConsumeCoverTail(projection, spans[d].High, projection.Row.Length);
            }
            else
            {
                MappedArray<ulong> index = _artifact.Index[d];
                ConsumeIndexTail(index, 0, spans[d].Low);
                ConsumeIndexTail(index, spans[d].High, index.Length);
            }
        }
        long bytes = covering ? visited * 4 + unique * 12 : visited * 8 + unique * 12;
        return (workspace.Complete(_artifact), visited, unique, bytes);

        void ConsumeCoverTail(CoverProjection projection, int start, int end)
        {
            for (int position = start; position < end; position++)
            {
                uint row = projection.Row[position];
                if (!workspace.FirstFact(row)) continue;
                float tests = projection.Tests[position];
                workspace.Add(projection.Mark[position],
                    (double)(projection.Values[Fields.Download][position] * tests), tests);
                unique++;
            }
        }

        void ConsumeIndexTail(MappedArray<ulong> index, int start, int end)
        {
            for (int position = start; position < end; position++)
            {
                uint row = unchecked((uint)index[position]);
                if (!workspace.FirstFact(row)) continue;
                int fact = (int)row;
                float tests = _artifact.Tests[fact];
                workspace.Add(_artifact.Mark[fact],
                    (double)(_artifact.Values[Fields.Download][fact] * tests), tests);
                unique++;
            }
        }
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) DeltaCore(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state, bool covering)
    {
        if (state.Complement)
            throw new InvalidOperationException("Delta state must be materialized from zero");
        if (!SameExceptBoundary(oldBox, newBox, dimension))
            throw new ArgumentException("Delta benchmark moves one lower boundary only");

        float from = oldBox.Low[dimension];
        float to = newBox.Low[dimension];
        bool contraction = to > from;
        float low = Math.Min(from, to);
        float high = Math.Max(from, to);
        long visited = 0;
        state.BeginChanged();
        if (covering)
        {
            CoverProjection projection = _artifact.Cover[dimension];
            int start = LowerBoundCover(projection, dimension, low, strict: false);
            int end = LowerBoundCover(projection, dimension, high, strict: false);
            visited = end - start;
            for (int position = start; position < end; position++)
            {
                if (!PassOtherCover(projection, position, oldBox, dimension)) continue;
                float tests = projection.Tests[position];
                double sign = contraction ? -1 : 1;
                uint mark = projection.Mark[position];
                state.Add(mark,
                    sign * (double)(projection.Values[Fields.Download][position] * tests),
                    sign * tests);
                state.MarkChanged(mark);
            }
        }
        else
        {
            MappedArray<ulong> index = _artifact.Index[dimension];
            int start = LowerBoundIndex(index, low, strict: false);
            int end = LowerBoundIndex(index, high, strict: false);
            visited = end - start;
            for (int position = start; position < end; position++)
            {
                uint row = unchecked((uint)index[position]);
                if (!PassOtherFact((int)row, oldBox, dimension)) continue;
                float tests = _artifact.Tests[(int)row];
                double sign = contraction ? -1 : 1;
                uint mark = _artifact.Mark[(int)row];
                state.Add(mark,
                    sign * (double)(_artifact.Values[Fields.Download][(int)row] * tests),
                    sign * tests);
                state.MarkChanged(mark);
            }
        }
        long bytes = visited * (covering ? 24 : 32);
        return (state.CompleteChanged(), visited, 0, bytes);
    }

    private (ExpectedResult Result, long Visited, long Unique, long Bytes) BitmapDeltaCore(
        RangeBox oldBox, RangeBox newBox, int dimension, QueryWorkspace state)
    {
        if (state.Complement)
            throw new InvalidOperationException("Delta state must be materialized from zero");
        if (!SameExceptBoundary(oldBox, newBox, dimension))
            throw new ArgumentException("Delta benchmark moves one lower boundary only");

        float from = oldBox.Low[dimension];
        float to = newBox.Low[dimension];
        bool contraction = to > from;
        float low = Math.Min(from, to);
        float high = Math.Max(from, to);
        CoverProjection projection = _artifact.Cover[dimension];
        int start = LowerBoundCover(projection, dimension, low, strict: false);
        int end = LowerBoundCover(projection, dimension, high, strict: false);
        long visited = end - start;

        state.EnsureFactBitmap(checked((int)_artifact.Manifest.FactCount));
        state.BeginChanged();
        for (int position = start; position < end; position++)
            state.MarkFact(projection.Row[position]);

        ulong[] bitmap = state.FactBitmap;
        int facts = checked((int)_artifact.Manifest.FactCount);
        for (int wordIndex = 0; wordIndex < bitmap.Length; wordIndex++)
        {
            ulong word = bitmap[wordIndex];
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                int row = (wordIndex << 6) + bit;
                if (row >= facts) break;
                if (PassOtherFact(row, oldBox, dimension))
                {
                    float tests = _artifact.Tests[row];
                    double sign = contraction ? -1 : 1;
                    uint mark = _artifact.Mark[row];
                    state.Add(mark,
                        sign * (double)(_artifact.Values[Fields.Download][row] * tests),
                        sign * tests);
                    state.MarkChanged(mark);
                }
                word &= word - 1;
            }
        }
        return (state.CompleteChanged(), visited, visited, visited * 28);
    }

    private bool PassFact(int row, RangeBox box)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            float value = _artifact.Values[d][row];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private bool PassOtherFact(int row, RangeBox box, int omitted)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            if (d == omitted) continue;
            float value = _artifact.Values[d][row];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private static bool PassCover(CoverProjection projection, int position, RangeBox box)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            float value = projection.Values[d][position];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private static bool PassOtherCover(CoverProjection projection, int position, RangeBox box, int omitted)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            if (d == omitted) continue;
            float value = projection.Values[d][position];
            if (value < box.Low[d] || value > box.High[d]) return false;
        }
        return true;
    }

    private static bool SameExceptBoundary(RangeBox oldBox, RangeBox newBox, int dimension)
    {
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            if (oldBox.High[d] != newBox.High[d]) return false;
            if (d != dimension && oldBox.Low[d] != newBox.Low[d]) return false;
        }
        return true;
    }

    private static (int Low, int High) IndexSpan(MappedArray<ulong> index, float low, float high) =>
        (LowerBoundIndex(index, low, strict: false), LowerBoundIndex(index, high, strict: true));

    private static int LowerBoundIndex(MappedArray<ulong> index, float value, bool strict)
    {
        uint key = FloatOrder.Sortable(value);
        int low = 0;
        int high = index.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            uint current = unchecked((uint)(index[middle] >> 32));
            if (current < key || (strict && current == key)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static (int Low, int High) CoverSpan(
        CoverProjection projection, int dimension, float low, float high) =>
        (LowerBoundCover(projection, dimension, low, strict: false),
         LowerBoundCover(projection, dimension, high, strict: true));

    private static int LowerBoundCover(
        CoverProjection projection, int dimension, float value, bool strict)
    {
        uint key = FloatOrder.Sortable(value);
        int low = 0;
        int high = projection.Row.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            uint current = FloatOrder.Sortable(projection.Values[dimension][middle]);
            if (current < key || (strict && current == key)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private QueryOutcome Measure(
        string engine, RangeBox box,
        Func<(ExpectedResult Result, long Visited, long Unique, long Bytes)> action)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long ws0 = process.WorkingSet64;
        TimeSpan cpu0 = process.TotalProcessorTime;
        int faults0 = ProcessMetrics.PageFaults();
        var io0 = ProcessMetrics.IoBytes();
        var sw = Stopwatch.StartNew();
        var result = action();
        sw.Stop();
        process.Refresh();
        var io1 = ProcessMetrics.IoBytes();
        return new QueryOutcome(
            engine, box.Name, sw.Elapsed.TotalMilliseconds,
            (process.TotalProcessorTime - cpu0).TotalMilliseconds,
            result.Result.Rows, result.Result.Checksum,
            result.Visited, result.Unique, result.Bytes,
            process.WorkingSet64, process.WorkingSet64 - ws0, process.PeakWorkingSet64,
            ProcessMetrics.PageFaults() - faults0,
            io1.ReadBytes - io0.ReadBytes,
            io1.WriteBytes - io0.WriteBytes);
    }
}
