using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Xunit;

namespace Colossus.Tests;

public class TileMathTests
{
    private static readonly Bbox Root = new(-10, -10, 10, 10);

    [Fact]
    public void RootTileRect_IsTheRootBbox()
    {
        var (xMin, yMin, xMax, yMax) = TileMath.TileRect(Root, new TileId(0, 0, 0));
        Assert.Equal(Root.MinX, xMin);
        Assert.Equal(Root.MinY, yMin);
        Assert.Equal(Root.MaxX, xMax);
        Assert.Equal(Root.MaxY, yMax);
    }

    [Fact]
    public void FourChildren_PartitionTheParent()
    {
        var parent = new TileId(1, 1, 0);
        var (pxMin, pyMin, pxMax, pyMax) = TileMath.TileRect(Root, parent);

        double area = 0;
        for (int q = 0; q < 4; q++)
        {
            var (xMin, yMin, xMax, yMax) = TileMath.TileRect(Root, parent.Child(q));
            Assert.InRange(xMin, pxMin, pxMax);
            Assert.InRange(xMax, pxMin, pxMax);
            Assert.InRange(yMin, pyMin, pyMax);
            Assert.InRange(yMax, pyMin, pyMax);
            area += (xMax - xMin) * (yMax - yMin);
        }
        Assert.Equal((pxMax - pxMin) * (pyMax - pyMin), area, precision: 9);
    }

    [Theory]
    [InlineData(-10, -10)]
    [InlineData(0, 0)]
    [InlineData(3.7, -8.2)]
    [InlineData(9.999, 9.999)]
    [InlineData(10, 10)]
    public void PointToTile_ReturnsTileWhoseRectContainsThePoint(double px, double py)
    {
        for (int z = 0; z <= 8; z++)
        {
            var (x, y) = TileMath.PointToTile(Root, z, px, py);
            Assert.True(TileMath.Contains(Root, new TileId(z, x, y), px, py),
                $"z={z}: ({px}, {py}) outside the rect of tile ({x}, {y})");
        }
    }

    [Fact]
    public void PointToTile_ClampsThePointOnTheMaxEdge()
    {
        var (x, y) = TileMath.PointToTile(Root, 3, Root.MaxX, Root.MaxY);
        Assert.Equal(7, x);
        Assert.Equal(7, y);
    }
}

public class BboxTests
{
    [Fact]
    public void ToPaddedSquare_IsSquare_AndContainsTheOriginal()
    {
        var b = new Bbox(0, 0, 10, 4);
        var s = b.ToPaddedSquare();
        Assert.Equal(s.SpanX, s.SpanY, precision: 12);
        Assert.True(s.MinX < b.MinX && s.MaxX > b.MaxX);
        Assert.True(s.MinY < b.MinY && s.MaxY > b.MaxY);
    }

    [Fact]
    public void ToPaddedSquare_DegenerateBbox_StillHasPositiveSpan()
    {
        var s = new Bbox(5, 5, 5, 5).ToPaddedSquare();
        Assert.True(s.SpanX > 0);
        Assert.Equal(s.SpanX, s.SpanY, precision: 12);
    }
}

public class TileIdTests
{
    [Fact]
    public void Children_CoverTheFourQuadrants()
    {
        var t = new TileId(2, 1, 3);
        Assert.Equal(new TileId(3, 2, 6), t.Child(0));
        Assert.Equal(new TileId(3, 3, 6), t.Child(1));
        Assert.Equal(new TileId(3, 2, 7), t.Child(2));
        Assert.Equal(new TileId(3, 3, 7), t.Child(3));
    }

    [Fact]
    public void RelativePath_IsZxyArrow() => Assert.Equal("4/2/9.arrow", new TileId(4, 2, 9).RelativePath);
}
