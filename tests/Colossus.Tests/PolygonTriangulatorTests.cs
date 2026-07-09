using Colossus.Infrastructure.Tiles;
using Xunit;

namespace Colossus.Tests;

public class PolygonTriangulatorTests
{
    [Fact]
    public void ClosedSquare_YieldsTwoTrianglesCoveringItsArea()
    {
        float[] square = [0, 0, 1, 0, 1, 1, 0, 1, 0, 0];
        var indices = PolygonTriangulator.Triangulate(square, [0, 5]);
        Assert.Equal(6, indices.Count);
        Assert.Equal(1.0, TriangleArea(square, indices), precision: 6);
    }

    [Fact]
    public void OpenRing_WithoutPartOffsets_AlsoTriangulates()
    {
        float[] square = [0, 0, 2, 0, 2, 2, 0, 2];
        var indices = PolygonTriangulator.Triangulate(square, null);
        Assert.Equal(6, indices.Count);
        Assert.Equal(4.0, TriangleArea(square, indices), precision: 6);
    }

    [Fact]
    public void ConcavePolygon_AreaIsPreserved()
    {
        // L-shape: 2x2 square minus its top-right 1x1 quadrant → area 3.
        float[] ell = [0, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2];
        var indices = PolygonTriangulator.Triangulate(ell, null);
        Assert.Equal(0, indices.Count % 3);
        Assert.Equal(3.0, TriangleArea(ell, indices), precision: 6);
    }

    [Fact]
    public void ClockwiseWinding_AreaIsPreserved()
    {
        float[] ellCw = [0, 0, 0, 2, 1, 2, 1, 1, 2, 1, 2, 0];
        var indices = PolygonTriangulator.Triangulate(ellCw, null);
        Assert.Equal(3.0, TriangleArea(ellCw, indices), precision: 6);
    }

    [Fact]
    public void MultiPart_TriangulatesEachPartIndependently()
    {
        // Two closed unit squares, offset in x.
        float[] coords =
        [
            0, 0, 1, 0, 1, 1, 0, 1, 0, 0,
            5, 0, 6, 0, 6, 1, 5, 1, 5, 0,
        ];
        var indices = PolygonTriangulator.Triangulate(coords, [0, 5, 10]);
        Assert.Equal(12, indices.Count);
        Assert.Equal(2.0, TriangleArea(coords, indices), precision: 6);
        // No triangle may span the two parts.
        for (int i = 0; i < indices.Count; i += 3)
        {
            bool firstPart = indices[i] < 5;
            Assert.Equal(firstPart, indices[i + 1] < 5);
            Assert.Equal(firstPart, indices[i + 2] < 5);
        }
    }

    [Fact]
    public void DegenerateInput_YieldsNoTriangles()
    {
        Assert.Empty(PolygonTriangulator.Triangulate([0, 0, 1, 1], null));
        Assert.Empty(PolygonTriangulator.Triangulate([], null));
        // A closed 2-vertex "ring" (first == last) has only one unique vertex.
        Assert.Empty(PolygonTriangulator.Triangulate([3, 3, 3, 3], [0, 2]));
    }

    [Fact]
    public void IndicesStayWithinTheVertexRange()
    {
        float[] ell = [0, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2];
        foreach (int i in PolygonTriangulator.Triangulate(ell, null))
            Assert.InRange(i, 0, ell.Length / 2 - 1);
    }

    private static double TriangleArea(float[] c, IReadOnlyList<int> indices)
    {
        double area = 0;
        for (int i = 0; i < indices.Count; i += 3)
        {
            double ax = c[2 * indices[i]], ay = c[2 * indices[i] + 1];
            double bx = c[2 * indices[i + 1]], by = c[2 * indices[i + 1] + 1];
            double dx = c[2 * indices[i + 2]], dy = c[2 * indices[i + 2] + 1];
            area += Math.Abs((bx - ax) * (dy - ay) - (dx - ax) * (by - ay)) / 2;
        }
        return area;
    }
}
