using Colossus.Domain.Model;

namespace Colossus.Domain.Tiling;

/// <summary>Maps between data-space coordinates and quadtree tiles over a padded, square bbox.
/// Y grows upward — we own bake and client, so we keep one convention rather than the TMS/XYZ flip.</summary>
public static class TileMath
{
    public static (double XMin, double YMin, double XMax, double YMax) TileRect(Bbox root, TileId tile)
    {
        int n = 1 << tile.Z;
        double cw = root.SpanX / n;
        double ch = root.SpanY / n;
        double xMin = root.MinX + tile.X * cw;
        double yMin = root.MinY + tile.Y * ch;
        return (xMin, yMin, xMin + cw, yMin + ch);
    }

    public static (int X, int Y) PointToTile(Bbox root, int z, double px, double py)
    {
        int n = 1 << z;
        int x = (int)Math.Floor((px - root.MinX) / root.SpanX * n);
        int y = (int)Math.Floor((py - root.MinY) / root.SpanY * n);
        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }
}
