namespace Colossus.Infrastructure.ClickHouse.Geometry;

/// <summary>The SQL fragments a geometry kind contributes to the canonical extract: optional derived
/// <paramref name="Prelude"/> columns (computed once, referenced by the rest), the representative
/// <paramref name="X"/>/<paramref name="Y"/>, and optional shape <paramref name="Extras"/> (the
/// <c>geometry</c> / <c>part_offsets</c> columns for non-point marks).</summary>
internal sealed record GeometrySql(
    IReadOnlyList<(string Expr, string Alias)> Prelude,
    string X, string Y,
    IReadOnlyList<(string Expr, string Alias)> Extras);
