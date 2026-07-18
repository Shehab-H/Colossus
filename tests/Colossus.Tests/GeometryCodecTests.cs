using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

/// <summary>Tile format 3 is lossless by construction: every codec reconstructs the format-2 geometry buffers
/// bit-for-bit. These pin the round trip (Decode∘Encode == BuildFormat2) and the per-tile codec choice.</summary>
public class GeometryCodecTests
{
    // The aggregate reducer's grid-cell ring order: [x0,y0, x1,y0, x1,y1, x0,y1, x0,y0], x0<x1.
    private static GeometryCodec.Row Rect(float x0, float y0, float x1, float y1) =>
        new([x0, y0, x1, y0, x1, y1, x0, y1, x0, y0], [0, 5]);

    private static void AssertRoundTrips(IReadOnlyList<GeometryCodec.Row> rows, byte expectedCodec)
    {
        var expected = GeometryCodec.BuildFormat2(rows);
        byte[] payload = GeometryCodec.Encode(rows);
        Assert.Equal(expectedCodec, payload[0]);

        var got = GeometryCodec.Decode(payload);
        Assert.Equal(expected.Positions, got.Positions); // bit-exact float[]
        Assert.Equal(expected.StartIndices, got.StartIndices);
        Assert.Equal(expected.Triangles, got.Triangles);
    }

    [Fact]
    public void Rect_tile_round_trips_bit_exact()
    {
        var rows = new List<GeometryCodec.Row>
        {
            Rect(0f, 0f, 1f, 1f),
            Rect(1f, 0f, 2f, 1f),
            Rect(0f, 1f, 1f, 2f),
            Rect(1.5f, 1.5f, 2.5f, 3.5f),
        };
        AssertRoundTrips(rows, GeometryCodec.CodecRect);
    }

    [Fact]
    public void Rect_tile_uses_shared_corner_tables()
    {
        // Distinct x corners {0,1,2}, y corners {0,1} → indices fit u16, tables carry exact floats.
        var rows = new List<GeometryCodec.Row> { Rect(0f, 0f, 1f, 1f), Rect(1f, 0f, 2f, 1f) };
        AssertRoundTrips(rows, GeometryCodec.CodecRect);
    }

    [Fact]
    public void Degenerate_rectangle_falls_to_delta()
    {
        // Zero-height "rect" is not a 2×2 rectangle → not rect-encodable.
        var rows = new List<GeometryCodec.Row> { new([0f, 0f, 1f, 0f, 2f, 0f, 0f, 0f], [0, 4]) };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }

    [Fact]
    public void Triangle_ring_round_trips_via_delta()
    {
        var rows = new List<GeometryCodec.Row>
        {
            new([0f, 0f, 4f, 0f, 2f, 3f, 0f, 0f], [0, 4]), // closed triangle
        };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }

    [Fact]
    public void Concave_and_multipart_rings_round_trip_via_delta()
    {
        var rows = new List<GeometryCodec.Row>
        {
            // An L-shaped (concave) closed ring.
            new([0f, 0f, 4f, 0f, 4f, 2f, 2f, 2f, 2f, 4f, 0f, 4f, 0f, 0f], [0, 7]),
            // Two rings in one row (outer + a smaller ring), each closed.
            new([0f, 0f, 6f, 0f, 6f, 6f, 0f, 6f, 0f, 0f, 2f, 2f, 4f, 2f, 4f, 4f, 2f, 4f, 2f, 2f], [0, 5, 10]),
        };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }

    [Fact]
    public void Mixed_rect_and_ring_tile_falls_to_delta_and_round_trips()
    {
        var rows = new List<GeometryCodec.Row>
        {
            Rect(0f, 0f, 1f, 1f),
            new([2f, 2f, 6f, 2f, 4f, 5f, 2f, 2f], [0, 4]), // a triangle → whole tile is delta
        };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }

    [Fact]
    public void Fractional_coordinates_are_bit_exact_through_delta()
    {
        var rows = new List<GeometryCodec.Row>
        {
            new([-71.4123f, 41.8231f, -71.4119f, 41.8235f, -71.4125f, 41.8240f, -71.4123f, 41.8231f], [0, 4]),
        };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }
}
