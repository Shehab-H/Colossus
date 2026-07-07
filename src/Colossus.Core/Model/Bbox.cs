namespace Colossus.Core.Model;

/// <summary>
/// Axis-aligned bounds over a view's two primary dimensions — lon/lat for geo,
/// arbitrary x/y for a chart. The quadtree is built inside this box.
/// </summary>
public readonly record struct Bbox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double SpanX => MaxX - MinX;
    public double SpanY => MaxY - MinY;

    /// <summary>
    /// Pad to a square and add a small margin so points exactly on the max edge fall inside
    /// tile arithmetic (floor never produces the out-of-range 2^z index).
    /// </summary>
    public Bbox ToPaddedSquare(double marginFraction = 0.001)
    {
        double cx = (MinX + MaxX) / 2.0;
        double cy = (MinY + MaxY) / 2.0;
        double half = Math.Max(SpanX, SpanY) / 2.0;
        if (half <= 0) half = 0.5; // degenerate (single point / empty) — arbitrary non-zero extent
        half *= 1.0 + marginFraction;
        return new Bbox(cx - half, cy - half, cx + half, cy + half);
    }
}
