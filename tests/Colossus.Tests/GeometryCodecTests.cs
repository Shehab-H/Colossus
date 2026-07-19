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
    public void Mixed_rectangle_windings_stay_rect_via_per_row_templates()
    {
        // Aggregate-order and quadkey-order rectangles in one tile (the internal-tile mix): different vertex
        // templates, but every row is still a rectangle, so the tile stays rect-encoded.
        var aggregate = new GeometryCodec.Row([0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 0f, 0f], [0, 5]);
        var quadkey = new GeometryCodec.Row([1f, 2f, 2f, 2f, 2f, 1f, 1f, 1f, 1f, 2f], [0, 5]);
        AssertRoundTrips([aggregate, quadkey, aggregate], GeometryCodec.CodecRect);
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

    [Fact]
    public void Row_wider_than_u16_uses_the_u32_triangle_width()
    {
        // A row whose local triangle indices exceed 65,535 — the width the codec picks must widen with the
        // data rather than truncate (admin1's largest ring is already 38,527). Built as many small closed
        // rings so the ear clipper stays linear; only the index magnitude is under test.
        const int parts = 16_385; // × 4 vertices = 65,540 > ushort.MaxValue
        var coords = new float[parts * 4 * 2];
        var offsets = new int[parts + 1];
        for (int p = 0; p < parts; p++)
        {
            float x = p * 0.5f, y = p * 0.25f;
            int b = p * 8;
            coords[b + 0] = x; coords[b + 1] = y;
            coords[b + 2] = x + 1f; coords[b + 3] = y;
            coords[b + 4] = x; coords[b + 5] = y + 1f;
            coords[b + 6] = x; coords[b + 7] = y; // closure
            offsets[p] = p * 4;
        }
        offsets[parts] = parts * 4;

        var rows = new List<GeometryCodec.Row> { new(coords, offsets) };
        byte[] payload = GeometryCodec.Encode(rows);
        Assert.Equal(GeometryCodec.CodecDelta, payload[0]);
        Assert.Equal(4, payload[10]); // triWidth, after codec/version/count/vertexCount

        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }

    [Fact]
    public void Part_offset_overrunning_the_row_is_clamped_identically_on_both_sides()
    {
        // A malformed intermediate offset (6 > the row's 4 vertices) with a correct final offset. The encoder
        // and PolygonTriangulator clamp the part end to the row's vertex count; the decoder must clamp the
        // same way or it slices the triangle stream differently and corrupts the tile.
        var rows = new List<GeometryCodec.Row>
        {
            new([0f, 0f, 4f, 0f, 2f, 3f, 0f, 0f], [0, 6, 4]),
        };
        AssertRoundTrips(rows, GeometryCodec.CodecDelta);
    }
}
