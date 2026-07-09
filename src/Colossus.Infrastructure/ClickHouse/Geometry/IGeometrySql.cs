using Colossus.Domain.Model;

namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>One geometry kind's normalization into the canonical <c>(x, y[, geometry, part_offsets])</c>
/// schema. Adding a source geometry (WKT, geohash, H3, …) is a new implementation of this — nothing
/// downstream of the extract changes (RULES R5).</summary>
internal interface IGeometrySql
{
    GeometrySql Build(GeometrySpec spec);
}
