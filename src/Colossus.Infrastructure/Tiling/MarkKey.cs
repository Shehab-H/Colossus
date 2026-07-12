using Colossus.Domain.Tiling;

namespace Colossus.Infrastructure.Tiling;

/// <summary>The per-mark key (<c>id</c> on tiles, <c>mk</c> on companions) that aligns a rendered mark
/// with its fact partials during the client fold. A real mark keys on its geometry identity — the
/// representative <c>(x, y)</c>, which is this engine's distinct-geometry key regardless of source; a
/// mark that merged into a ~1px grid cell keys on that cell. Both sides (marks tile and fact companion)
/// derive the key from these same expressions, so equal marks produce equal keys by construction.</summary>
public static class MarkKey
{
    /// <summary>Real (unmerged) mark key from its representative point.</summary>
    public static string RealSql(string x = TileSchema.X, string y = TileSchema.Y) =>
        $"('p:' || CAST({x} AS VARCHAR) || ':' || CAST({y} AS VARCHAR))";

    /// <summary>Merged mark key from its grid cell (matches the reducer's sub-pixel collapse).</summary>
    public static string MergedSql(string gx, string gy) =>
        $"('g:' || CAST({gx} AS VARCHAR) || ':' || CAST({gy} AS VARCHAR))";
}
