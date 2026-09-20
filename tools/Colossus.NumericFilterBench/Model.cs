using System.Text.Json;

namespace Colossus.NumericFilterBench;

internal static class Fields
{
    public static readonly string[] Names = ["sample_share", "download_mbps", "upload_mbps", "latency_ms"];
    public const int Sample = 0;
    public const int Download = 1;
    public const int Upload = 2;
    public const int Latency = 3;
}

internal sealed record QuantileSet(
    float Q01, float Q05, float Q10, float Q25, float Q45,
    float Q55, float Q75, float Q90, float Q95, float Q99);

internal sealed record ArtifactManifest(
    int Version,
    long FactCount,
    int MarkCount,
    int MaxFactsPerMark,
    string[] Dimensions,
    QuantileSet[] Quantiles,
    Dictionary<string, ExpectedResult> Expected,
    double BuildSeconds,
    long ArtifactBytes)
{
    public const string FileName = "manifest.json";

    public static ArtifactManifest Read(string directory) =>
        JsonSerializer.Deserialize<ArtifactManifest>(
            File.ReadAllText(Path.Combine(directory, FileName)), JsonOptions())
        ?? throw new InvalidDataException("Invalid numeric benchmark manifest");

    public void Write(string directory) => File.WriteAllText(
        Path.Combine(directory, FileName),
        JsonSerializer.Serialize(this, JsonOptions()));

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
}

internal sealed record ExpectedResult(long Rows, ulong Checksum);

internal readonly record struct RangeBox(float[] Low, float[] High, string Name)
{
    public static RangeBox Quantile(ArtifactManifest manifest, int low, int high)
    {
        static float At(QuantileSet q, int p) => p switch
        {
            1 => q.Q01, 5 => q.Q05, 10 => q.Q10, 25 => q.Q25, 45 => q.Q45,
            55 => q.Q55, 75 => q.Q75, 90 => q.Q90, 95 => q.Q95, 99 => q.Q99,
            _ => throw new ArgumentOutOfRangeException(nameof(p))
        };

        var lows = new float[Fields.Names.Length];
        var highs = new float[Fields.Names.Length];
        for (int d = 0; d < Fields.Names.Length; d++)
        {
            lows[d] = At(manifest.Quantiles[d], low);
            highs[d] = At(manifest.Quantiles[d], high);
        }
        return new RangeBox(lows, highs, $"q{low:00}-q{high:00}");
    }

    public RangeBox WithLower(int dimension, float value, string name)
    {
        float[] low = (float[])Low.Clone();
        low[dimension] = value;
        return new RangeBox(low, (float[])High.Clone(), name);
    }
}

internal static class FloatOrder
{
    public static uint Sortable(float value)
    {
        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(value));
        return (bits & 0x8000_0000u) != 0 ? ~bits : bits ^ 0x8000_0000u;
    }

    public static float FromSortable(uint value)
    {
        uint bits = (value & 0x8000_0000u) != 0 ? value ^ 0x8000_0000u : ~value;
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }
}
