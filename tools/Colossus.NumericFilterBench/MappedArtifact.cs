using System.IO.MemoryMappedFiles;

namespace Colossus.NumericFilterBench;

internal unsafe sealed class MappedArray<T> : IDisposable where T : unmanaged
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _acquired;
    private readonly T* _pointer;

    public MappedArray(string path, long expectedLength)
    {
        long bytes = new FileInfo(path).Length;
        long itemSize = sizeof(T);
        if (bytes % itemSize != 0 || bytes / itemSize != expectedLength)
            throw new InvalidDataException($"Unexpected plane length for {path}: {bytes} bytes");
        Length = checked((int)expectedLength);
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* acquired = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref acquired);
        _acquired = acquired;
        _pointer = (T*)(acquired + _view.PointerOffset);
    }

    public int Length { get; }
    public T* Pointer => _pointer;
    public ref readonly T this[int index] => ref _pointer[index];

    public void Dispose()
    {
        if (_acquired != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _acquired = null;
        }
        _view.Dispose();
        _file.Dispose();
    }
}

internal unsafe sealed class CoverProjection : IDisposable
{
    public CoverProjection(string directory, string dimension, int facts)
    {
        Row = new(Path.Combine(directory, $"cover-{dimension}-row.u32"), facts);
        Mark = new(Path.Combine(directory, $"cover-{dimension}-mark.u32"), facts);
        Tests = new(Path.Combine(directory, $"cover-{dimension}-tests.f32"), facts);
        Values = Fields.Names.Select(field =>
            new MappedArray<float>(Path.Combine(directory, $"cover-{dimension}-{field}.f32"), facts)).ToArray();
    }

    public MappedArray<uint> Row { get; }
    public MappedArray<uint> Mark { get; }
    public MappedArray<float> Tests { get; }
    public MappedArray<float>[] Values { get; }

    public void Dispose()
    {
        Row.Dispose();
        Mark.Dispose();
        Tests.Dispose();
        foreach (MappedArray<float> value in Values) value.Dispose();
    }
}

internal unsafe sealed class MappedArtifact : IDisposable
{
    public MappedArtifact(string directory)
    {
        Directory = directory;
        Manifest = ArtifactManifest.Read(directory);
        if (Manifest.FactCount > int.MaxValue)
            throw new NotSupportedException("Shard is too large for the benchmark row-ID width");
        int facts = checked((int)Manifest.FactCount);
        int marks = Manifest.MarkCount;
        Mark = new(Path.Combine(directory, "fact-mark.u32"), facts);
        Tests = new(Path.Combine(directory, "fact-tests.f32"), facts);
        Values = Fields.Names.Select(field =>
            new MappedArray<float>(Path.Combine(directory, $"fact-{field}.f32"), facts)).ToArray();
        Index = Fields.Names.Select(field =>
            new MappedArray<ulong>(Path.Combine(directory, $"index-{field}.u64"), facts)).ToArray();
        Offsets = new(Path.Combine(directory, "mark-offsets.u32"), marks + 1L);
        BaseNumerator = new(Path.Combine(directory, "mark-base-numerator.f64"), marks);
        BaseDenominator = new(Path.Combine(directory, "mark-base-denominator.f64"), marks);
        Min = Fields.Names.Select(field =>
            new MappedArray<float>(Path.Combine(directory, $"mark-{field}-min.f32"), marks)).ToArray();
        Max = Fields.Names.Select(field =>
            new MappedArray<float>(Path.Combine(directory, $"mark-{field}-max.f32"), marks)).ToArray();
        Cover = Fields.Names.Select(field => new CoverProjection(directory, field, facts)).ToArray();
    }

    public string Directory { get; }
    public ArtifactManifest Manifest { get; }
    public MappedArray<uint> Mark { get; }
    public MappedArray<float> Tests { get; }
    public MappedArray<float>[] Values { get; }
    public MappedArray<ulong>[] Index { get; }
    public MappedArray<uint> Offsets { get; }
    public MappedArray<double> BaseNumerator { get; }
    public MappedArray<double> BaseDenominator { get; }
    public MappedArray<float>[] Min { get; }
    public MappedArray<float>[] Max { get; }
    public CoverProjection[] Cover { get; }

    public void Dispose()
    {
        Mark.Dispose();
        Tests.Dispose();
        foreach (var item in Values) item.Dispose();
        foreach (var item in Index) item.Dispose();
        Offsets.Dispose();
        BaseNumerator.Dispose();
        BaseDenominator.Dispose();
        foreach (var item in Min) item.Dispose();
        foreach (var item in Max) item.Dispose();
        foreach (var item in Cover) item.Dispose();
    }
}
