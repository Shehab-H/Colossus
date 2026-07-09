using Colossus.Domain.Model;
using Colossus.Domain.Sources;

namespace Colossus.Application;

/// <summary>The bake plan derived from a source's shape — everything the reduce stage needs that is
/// not authored in the view config.</summary>
/// <param name="Reduction">The primitive chosen from cardinality (never the chart's name).</param>
/// <param name="MaxZoom">A depth cap; adaptive recursion stops earlier where a node is under budget.</param>
/// <param name="TilePointBudget">Max real marks per leaf tile.</param>
/// <param name="Root">The padded, square root bbox all tiling runs over.</param>
public sealed record BakePlan(ReductionKind Reduction, int MaxZoom, int TilePointBudget, Bbox Root);

/// <summary>Turns a source probe into a <see cref="BakePlan"/>. The single place bake planning lives:
/// reduction choice, depth cap, budget, and root bbox. Chooses from the data's *shape* — how many
/// source rows, and how many are distinct shapes — never from the chart's name or an authored field
/// (RULES R6, PLAN "reduction chosen from the data + encoding").
///
/// <list type="bullet">
/// <item><b>RawPassthrough</b> — the whole source is already under a tile budget: ship it as one tile.</item>
/// <item><b>Aggregate</b> — the chart is definitionally an aggregate: an area mark (polygon/rect/heat),
///   or a fact cube layered over relatively few shapes. Coarse levels are honest means of children,
///   never samples.</item>
/// <item><b>QuadtreeLod</b> — a genuine point cloud (≈one row per shape, more shapes than fit): a
///   spatial pyramid whose coarse levels are a labeled, resolvable preview.</item>
/// </list></summary>
public sealed class BakePlanner
{
    public const int DefaultTilePointBudget = 250_000;

    // Above this many rows per distinct shape, the source is a fact cube over few shapes (e.g. a
    // time × dimension series per cell), which only makes sense to render as an aggregate.
    private const double CubeRowsPerShape = 4.0;

    private readonly int _tilePointBudget;

    public BakePlanner(int tilePointBudget = DefaultTilePointBudget) => _tilePointBudget = tilePointBudget;

    public BakePlan Plan(SourceBounds probe, ViewConfig view)
    {
        Bbox root = probe.Bounds.ToPaddedSquare();
        var reduction = SelectReduction(probe, view);
        int maxZoom = PlanMaxZoom(probe.DistinctGeometries);
        return new BakePlan(reduction, maxZoom, _tilePointBudget, root);
    }

    private ReductionKind SelectReduction(SourceBounds probe, ViewConfig view)
    {
        if (probe.Count <= _tilePointBudget)
            return ReductionKind.RawPassthrough;

        bool areaMark = view.Mark is Mark.Polygon or Mark.Rect or Mark.Heat;
        double rowsPerShape = probe.DistinctGeometries > 0
            ? (double)probe.Count / probe.DistinctGeometries
            : 1.0;

        if (areaMark || rowsPerShape >= CubeRowsPerShape)
            return ReductionKind.Aggregate;

        return ReductionKind.QuadtreeLod;
    }

    // Depth is driven by distinct shapes, not rows: a fact cube has many rows per shape, but it is the
    // shapes that must land in leaves under budget. Enough depth for even a clustered region's leaves to
    // reach the budget; the adaptive recursion stops earlier wherever a node is already under budget, so
    // a generous cap is free (sparse regions never use the extra depth). The margin absorbs non-uniform
    // density (dense cities vs. empty ocean).
    private int PlanMaxZoom(long distinctShapes)
    {
        double perAxis = Math.Sqrt((double)distinctShapes / _tilePointBudget);
        int z = (int)Math.Ceiling(Math.Log2(Math.Max(perAxis, 1))) + 5;
        return Math.Clamp(z, 1, 16);
    }
}
