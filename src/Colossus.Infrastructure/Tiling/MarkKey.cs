using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.Tiling;

/// <summary>The per-mark key (<c>id</c> on tiles, <c>mk</c> on companions) that aligns a rendered mark
/// with its fact partials during the client fold. A real mark keys on the exact canonical geometry the
/// extract already baked (point x/y or shape coordinates/parts); a mark that merged into a ~1px grid
/// cell keys on that cell. The authored config and public tile schema remain unchanged.</summary>
public static class MarkKey
{
    /// <summary>Internal-only 32-byte column persisted with fold facts so geometry hashing happens during
    /// the bake, never on a request. It is deliberately outside the authored channel namespace.</summary>
    public const string GeometryIdentity = "__colossus_geometry_key";

    /// <summary>A source-neutral, versioned fingerprint of the canonical staging representation. Shape
    /// identity intentionally uses the exact float coordinates and part boundaries sent to rendering,
    /// not the source's WKT/quadkey spelling or the lossy representative point.</summary>
    public static string GeometryIdentitySql(
        bool hasShape,
        string x = TileSchema.X,
        string y = TileSchema.Y,
        string geometry = TileSchema.Geometry,
        string partOffsets = TileSchema.PartOffsets)
    {
        string payload = hasShape
            ? $"struct_pack(v := 1, kind := 'shape', coordinates := {geometry}, parts := {partOffsets})"
            : $"struct_pack(v := 1, kind := 'point', x := {x}, y := {y})";
        return $"unhex(sha256(CAST(to_json({payload}) AS VARCHAR)))";
    }

    /// <summary>Real (unmerged) mark key from the baked geometry fingerprint.</summary>
    public static string RealSql(string geometryIdentity = GeometryIdentity) =>
        $"('p:' || lower(hex({geometryIdentity})))";

    /// <summary>Compatibility key for releases baked before geometry fingerprints were persisted.</summary>
    public static string LegacyRealSql(string x = TileSchema.X, string y = TileSchema.Y) =>
        $"('p:' || CAST({x} AS VARCHAR) || ':' || CAST({y} AS VARCHAR))";

    /// <summary>Merged mark key from its grid cell (matches the reducer's sub-pixel collapse).</summary>
    public static string MergedSql(string gx, string gy) =>
        $"('g:' || CAST({gx} AS VARCHAR) || ':' || CAST({gy} AS VARCHAR))";
}
