using System.Text.Json;
using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Cross-language authority for tile format 3 (mirrors the tiling/measure/slab fixtures): a set of
/// small hand-checkable tiles in each geometry encoding, with the exact payload bytes and the decoded
/// format-2 buffers. This C# test pins the encoder+decoder against the committed fixture; the web Vitest
/// (geometryCodec.test.ts) decodes the same bytes and must land on the same buffers. A rename or format drift
/// fails both suites. Set <c>COLOSSUS_REGEN_FIXTURES=1</c> to regenerate after an intended format change.</summary>
public class GeometryFixtureTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "fixtures", "geometry-codec-cases.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // Each case's input rows (interleaved coords + part offsets), in the aggregate reducer's grid-cell ring
    // order for the rect case and real rings for the delta cases.
    private static IReadOnlyList<(string Name, GeometryCodec.Row[] Rows)> Cases() =>
    [
        ("rect-grid", [
            new([0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 0f, 0f], [0, 5]),
            new([1f, 0f, 2f, 0f, 2f, 1f, 1f, 1f, 1f, 0f], [0, 5]),
            new([0f, 1f, 1f, 1f, 1f, 2f, 0f, 2f, 0f, 1f], [0, 5]),
        ]),
        ("delta-triangle", [
            new([0f, 0f, 4f, 0f, 2f, 3f, 0f, 0f], [0, 4]),
        ]),
        ("delta-multipart", [
            new([0f, 0f, 6f, 0f, 6f, 6f, 0f, 6f, 0f, 0f, 2f, 2f, 4f, 2f, 4f, 4f, 2f, 4f, 2f, 2f], [0, 5, 10]),
        ]),
        ("delta-fractional", [
            new([-71.4123f, 41.8231f, -71.4119f, 41.8235f, -71.4125f, 41.8240f, -71.4123f, 41.8231f], [0, 4]),
        ]),
    ];

    private sealed record Case(string Name, byte Codec, string PayloadBase64,
        float[] Positions, int[] StartIndices, int[] Triangles);
    private sealed record Fixture(Case[] Cases);

    private static Fixture Build()
    {
        var cases = Cases().Select(c =>
        {
            byte[] payload = GeometryCodec.Encode(c.Rows);
            var decoded = GeometryCodec.Decode(payload);
            // The codec's own inverse and the writer's ground truth must already agree (GeometryCodecTests);
            // asserting it here too keeps a regenerated fixture from ever baking in a lossy payload.
            var truth = GeometryCodec.BuildFormat2(c.Rows);
            Assert.Equal(truth.Positions, decoded.Positions);
            Assert.Equal(truth.StartIndices, decoded.StartIndices);
            Assert.Equal(truth.Triangles, decoded.Triangles);
            return new Case(c.Name, payload[0], Convert.ToBase64String(payload),
                decoded.Positions, decoded.StartIndices, decoded.Triangles);
        }).ToArray();
        return new Fixture(cases);
    }

    [Fact]
    public void Fixture_matches_the_codec()
    {
        var built = Build();

        if (Environment.GetEnvironmentVariable("COLOSSUS_REGEN_FIXTURES") == "1" || !File.Exists(FixturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
            File.WriteAllText(FixturePath, JsonSerializer.Serialize(built, Json));
        }

        var committed = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath), Json)!;
        Assert.Equal(built.Cases.Length, committed.Cases.Length);
        for (int i = 0; i < built.Cases.Length; i++)
        {
            var b = built.Cases[i];
            var c = committed.Cases[i];
            Assert.Equal(b.Name, c.Name);
            Assert.Equal(b.Codec, c.Codec);
            Assert.Equal(b.PayloadBase64, c.PayloadBase64); // pins the encoder byte-for-byte
            Assert.Equal(b.Positions, c.Positions);
            Assert.Equal(b.StartIndices, c.StartIndices);
            Assert.Equal(b.Triangles, c.Triangles);
        }
    }
}
