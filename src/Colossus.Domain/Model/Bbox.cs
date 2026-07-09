using System.Text.Json.Serialization;

namespace Colossus.Domain.Model;

/// <summary>Axis-aligned bounds over a view's two primary dimensions (lon/lat or arbitrary x/y).</summary>
public readonly record struct Bbox(double MinX, double MinY, double MaxX, double MaxY)
{
    [JsonIgnore] public double SpanX => MaxX - MinX;
    [JsonIgnore] public double SpanY => MaxY - MinY;

    /// <summary>Pad to a square with a small margin so points on the max edge stay inside tile arithmetic.</summary>
    public Bbox ToPaddedSquare(double marginFraction = 0.001)
    {
        double cx = (MinX + MaxX) / 2.0;
        double cy = (MinY + MaxY) / 2.0;
        double half = Math.Max(SpanX, SpanY) / 2.0;
        if (half <= 0) half = 0.5;
        half *= 1.0 + marginFraction;
        return new Bbox(cx - half, cy - half, cx + half, cy + half);
    }
}
