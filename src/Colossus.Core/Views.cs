using Colossus.Core.Model;

namespace Colossus.Core;

/// <summary>
/// The M1 view catalog — the single source of truth Seed (table creation) and Bake (what to bake)
/// both reference. Two views deliberately exercise the same engine through different viewports:
/// a geographic map and a non-geo Cartesian scatter.
/// </summary>
public static class Views
{
    public const string Database = "colossus";

    public static readonly ViewDescriptor Geo = new()
    {
        Id = "geo-points",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Reduction = ReductionKind.QuadtreeLod,
        Source = new SourceSpec
        {
            Table = $"{Database}.points_geo",
            XColumn = "lon",
            YColumn = "lat",
            ValueColumn = "value",
            CategoryColumn = "category",
        },
    };

    public static readonly ViewDescriptor Scatter = new()
    {
        Id = "xy-scatter",
        Viewport = Viewport.Orthographic,
        Mark = Mark.Point,
        Reduction = ReductionKind.QuadtreeLod,
        Source = new SourceSpec
        {
            Table = $"{Database}.points_xy",
            XColumn = "x",
            YColumn = "y",
            ValueColumn = "value",
            CategoryColumn = "category",
        },
    };

    public static readonly IReadOnlyList<ViewDescriptor> All = new[] { Geo, Scatter };

    public static ViewDescriptor ById(string id) =>
        All.FirstOrDefault(v => v.Id == id)
        ?? throw new ArgumentException($"Unknown view id '{id}'. Known: {string.Join(", ", All.Select(v => v.Id))}");
}
