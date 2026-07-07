using Colossus.Core.Model;

namespace Colossus.Core;

/// <summary>
/// Maps between data-space coordinates and quadtree tiles. The pyramid divides a (padded, square)
/// bbox into a 2^z x 2^z grid at each zoom level z. Y grows upward — we own both the bake and the
/// client, so we keep one consistent convention rather than the TMS/XYZ flip.
/// </summary>
public static class TileMath
{
    /// <summary>The data-space rectangle covered by a tile.</summary>
    public static (double XMin, double YMin, double XMax, double YMax) TileRect(Bbox root, TileId tile)
    {
        int n = 1 << tile.Z;
        double cw = root.SpanX / n;
        double ch = root.SpanY / n;
        double xMin = root.MinX + tile.X * cw;
        double yMin = root.MinY + tile.Y * ch;
        return (xMin, yMin, xMin + cw, yMin + ch);
    }

    /// <summary>The tile grid coords a point falls into at a given zoom level (clamped to valid range).</summary>
    public static (int X, int Y) PointToTile(Bbox root, int z, double px, double py)
    {
        int n = 1 << z;
        int x = (int)Math.Floor((px - root.MinX) / root.SpanX * n);
        int y = (int)Math.Floor((py - root.MinY) / root.SpanY * n);
        return (Math.Clamp(x, 0, n - 1), Math.Clamp(y, 0, n - 1));
    }
}
