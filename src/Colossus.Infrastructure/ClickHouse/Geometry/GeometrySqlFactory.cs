using Colossus.Domain.Model;

namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>Resolves a <see cref="GeometryKind"/> to its <see cref="IGeometrySql"/> normalization.
/// This switch is the *only* place that grows when a source geometry is added.</summary>
internal static class GeometrySqlFactory
{
    public static GeometrySql Build(GeometrySpec spec) => Resolve(spec.Kind).Build(spec);

    private static IGeometrySql Resolve(GeometryKind kind) => kind switch
    {
        GeometryKind.Xy or GeometryKind.LonLat => new PointGeometrySql(),
        GeometryKind.Quadkey => new QuadkeyGeometrySql(),
        GeometryKind.Wkt => new WktGeometrySql(),
        _ => throw new NotSupportedException(
            $"ClickHouse adapter: geometry kind '{kind}' is not implemented yet."),
    };
}
