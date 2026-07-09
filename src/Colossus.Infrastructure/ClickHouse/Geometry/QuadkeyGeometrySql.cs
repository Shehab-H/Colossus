using Colossus.Domain.Model;
using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>Bing quadkey → tile index (decoded once in the prelude), then a centroid point plus the
/// cell's four corners as a closed, interleaved lon/lat ring. Zoom-independent: ring math uses the key
/// length, so a level-10 and a level-20 key both normalize the same way.</summary>
internal sealed class QuadkeyGeometrySql : IGeometrySql
{
    public GeometrySql Build(GeometrySpec spec)
    {
        string col = spec.Column!;
        string chars = $"extractAll({col}, '.')";
        string tx = $"arraySum((c, i) -> if(c IN ('1', '3'), bitShiftLeft(toUInt64(1), toUInt64(length({col}) - i)), toUInt64(0)), {chars}, arrayEnumerate({chars}))";
        string ty = $"arraySum((c, i) -> if(c IN ('2', '3'), bitShiftLeft(toUInt64(1), toUInt64(length({col}) - i)), toUInt64(0)), {chars}, arrayEnumerate({chars}))";
        string size = $"bitShiftLeft(toUInt64(1), toUInt64(length({col})))";

        (string, string)[] prelude = [(tx, "_tx"), (ty, "_ty"), (size, "_size")];

        string Lon(string off) => $"(_tx + {off}) / _size * 360 - 180";
        string Lat(string off) => $"degrees(atan(sinh(pi() * (1 - 2 * (_ty + {off}) / _size))))";

        string x = $"toFloat32({Lon("0.5")})";
        string y = $"toFloat32({Lat("0.5")})";
        string ring = $"[{Lon("0")}, {Lat("0")}, {Lon("1")}, {Lat("0")}, {Lon("1")}, {Lat("1")}, {Lon("0")}, {Lat("1")}, {Lon("0")}, {Lat("0")}]";
        (string, string)[] extras =
        [
            ($"arrayMap(v -> toFloat32(v), {ring})", TileSchema.Geometry),
            ("[toInt32(0), toInt32(5)]", TileSchema.PartOffsets),
        ];
        return new GeometrySql(prelude, x, y, extras);
    }
}
