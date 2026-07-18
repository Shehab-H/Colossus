using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>WKT POLYGON / MULTIPOLYGON → the canonical schema: rings flattened to one interleaved
/// lon/lat list with <c>part_offsets</c> delimiting each ring, and the vertex mean as the
/// representative point. Rings keep the WKT closure (first vertex repeated last), matching the
/// closed rings every other geometry kind emits.</summary>
internal sealed class WktGeometrySql : IGeometrySql
{
    public GeometrySql Build(GeometrySpec spec)
    {
        string col = spec.Column!;
        // Normalize both kinds to MultiPolygon = Array(Array(Ring)); Ring = Array((lon, lat)).
        string mp = $"if(startsWith({col}, 'MULTIPOLYGON'), readWKTMultiPolygon({col}), [readWKTPolygon({col})])";

        (string, string)[] prelude =
        [
            (mp, "_mp"),
            ("arrayReduce('groupArrayArray', _mp)", "_rings"), // one level flat: Array(Ring)
            ("arrayFlatten(_mp)", "_pts"), // tuples stop the recursion: Array(Point)
        ];

        string x = "toFloat32(arrayAvg(p -> p.1, _pts))";
        string y = "toFloat32(arrayAvg(p -> p.2, _pts))";
        (string, string)[] extras =
        [
            ($"arrayMap(v -> toFloat32(v), arrayFlatten(arrayMap(p -> [p.1, p.2], _pts)))", TileSchema.Geometry),
            ($"arrayMap(v -> toInt32(v), arrayPushFront(arrayCumSum(arrayMap(r -> length(r), _rings)), 0))", TileSchema.PartOffsets),
        ];
        return new GeometrySql(prelude, x, y, extras);
    }
}
