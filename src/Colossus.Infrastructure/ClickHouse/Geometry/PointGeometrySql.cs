using Colossus.Domain.Model;

namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>Point sources — <c>xy</c> (arbitrary x/y) and <c>lonLat</c> (geo) — both normalize to a
/// representative point straight from two columns; there is no shape geometry.</summary>
internal sealed class PointGeometrySql : IGeometrySql
{
    public GeometrySql Build(GeometrySpec spec)
    {
        var (x, y) = spec.Kind == GeometryKind.LonLat ? (spec.Lon!, spec.Lat!) : (spec.X!, spec.Y!);
        return new GeometrySql([], $"toFloat32({x})", $"toFloat32({y})", []);
    }
}
