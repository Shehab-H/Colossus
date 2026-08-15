using System.Globalization;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Model;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Colossus.Infrastructure.Sources;

internal sealed record CanonicalGeometry(float X, float Y, float[]? Coordinates, int[]? PartOffsets);

/// <summary>Adapter-neutral normalization of supported source geometry representations into the
/// canonical point/shape staging contract.</summary>
internal static class CanonicalGeometryNormalizer
{
    public static CanonicalGeometry Normalize(GeometrySpec spec, Func<string, object?> value) => spec.Kind switch
    {
        GeometryKind.Xy => Point(spec, value(spec.X!), value(spec.Y!), spec.Geographic),
        GeometryKind.LonLat => Point(spec, value(spec.Lon!), value(spec.Lat!), geographic: true),
        GeometryKind.Quadkey => Quadkey(spec, value(spec.Column!)),
        GeometryKind.Wkt => Wkt(spec, value(spec.Column!)),
        _ => throw GeometryError($"Geometry kind '{spec.Kind}' is not supported."),
    };

    private static CanonicalGeometry Point(GeometrySpec spec, object? xValue, object? yValue, bool geographic)
    {
        double x = Number(xValue, spec.Kind == GeometryKind.LonLat ? "longitude" : "x");
        double y = Number(yValue, spec.Kind == GeometryKind.LonLat ? "latitude" : "y");
        Validate(x, y, geographic);
        return new CanonicalGeometry((float)x, (float)y, null, null);
    }

    private static CanonicalGeometry Quadkey(GeometrySpec spec, object? value)
    {
        string key = Text(value, spec.Column!);
        if (key.Length is 0 or > 31 || key.Any(character => character is < '0' or > '3'))
            throw GeometryError("Mapped quadkeys must contain 1–31 digits in the range 0–3.");

        long tileX = 0, tileY = 0;
        for (int index = 0; index < key.Length; index++)
        {
            long mask = 1L << (key.Length - index - 1);
            if (key[index] is '1' or '3') tileX |= mask;
            if (key[index] is '2' or '3') tileY |= mask;
        }
        double size = 1L << key.Length;
        double Lon(double offset) => (tileX + offset) / size * 360d - 180d;
        double Lat(double offset) => Math.Atan(Math.Sinh(Math.PI * (1d - 2d * (tileY + offset) / size))) * 180d / Math.PI;

        double x = Lon(0.5), y = Lat(0.5);
        float[] ring =
        [
            (float)Lon(0), (float)Lat(0),
            (float)Lon(1), (float)Lat(0),
            (float)Lon(1), (float)Lat(1),
            (float)Lon(0), (float)Lat(1),
            (float)Lon(0), (float)Lat(0),
        ];
        Validate(x, y, geographic: true);
        return new CanonicalGeometry((float)x, (float)y, ring, [0, 5]);
    }

    private static CanonicalGeometry Wkt(GeometrySpec spec, object? value)
    {
        string wkt = Text(value, spec.Column!);
        Geometry geometry;
        try { geometry = new WKTReader().Read(wkt); }
        catch (Exception error)
        {
            throw GeometryError("Mapped WKT must be a valid POLYGON or MULTIPOLYGON.", error);
        }

        Polygon[] polygons = geometry switch
        {
            Polygon polygon => [polygon],
            MultiPolygon multi => Enumerable.Range(0, multi.NumGeometries)
                .Select(index => (Polygon)multi.GetGeometryN(index)).ToArray(),
            _ => throw GeometryError("Mapped WKT must be a POLYGON or MULTIPOLYGON."),
        };
        if (polygons.Length == 0 || geometry.IsEmpty)
            throw GeometryError("Mapped WKT geometry is empty.");

        var coordinates = new List<float>();
        var offsets = new List<int> { 0 };
        double sumX = 0, sumY = 0;
        int points = 0;
        foreach (Polygon polygon in polygons)
        {
            AddRing(polygon.ExteriorRing);
            for (int index = 0; index < polygon.NumInteriorRings; index++) AddRing(polygon.GetInteriorRingN(index));
        }
        if (points == 0) throw GeometryError("Mapped WKT geometry has no coordinates.");
        double x = sumX / points, y = sumY / points;
        Validate(x, y, geographic: true);
        return new CanonicalGeometry((float)x, (float)y, [.. coordinates], [.. offsets]);

        void AddRing(LineString ring)
        {
            foreach (Coordinate coordinate in ring.Coordinates)
            {
                Validate(coordinate.X, coordinate.Y, geographic: true);
                coordinates.Add((float)coordinate.X);
                coordinates.Add((float)coordinate.Y);
                sumX += coordinate.X;
                sumY += coordinate.Y;
                points++;
            }
            offsets.Add(points);
        }
    }

    private static double Number(object? value, string field)
    {
        try
        {
            if (value is null or DBNull) throw new InvalidCastException();
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            throw GeometryError($"Mapped {field} values must be finite numbers.", error);
        }
    }

    private static string Text(object? value, string field)
    {
        if (value is null or DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
            throw GeometryError($"Mapped geometry column '{field}' contains an empty value.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim();
    }

    private static void Validate(double x, double y, bool geographic)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            throw GeometryError("Mapped geometry contains null, empty, or non-finite coordinates.");
        if (geographic && (x is < -180 or > 180 || y is < -90 or > 90))
            throw GeometryError("Mapped geographic coordinates fall outside longitude/latitude bounds; verify that the columns are not swapped.");
    }

    private static SourceOperationException GeometryError(string message, Exception? inner = null) => new(
        SourceErrorKind.Type,
        message,
        new Dictionary<string, string[]> { ["source.geometry"] = [message] },
        innerException: inner);
}
