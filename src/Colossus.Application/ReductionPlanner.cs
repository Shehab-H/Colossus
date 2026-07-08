using Colossus.Domain.Model;
using Colossus.Domain.Sources;

namespace Colossus.Application;

/// <summary>Chooses the reduction primitive from the data's *shape*, not the chart's name and not an
/// authored field (RULES R6, PLAN "reduction chosen from the data + encoding"). The signal is
/// cardinality: how many source rows, and how many of them are distinct shapes.
///
/// <list type="bullet">
/// <item><b>RawPassthrough</b> — the whole source is already under a tile budget: ship it as one tile.</item>
/// <item><b>Aggregate</b> — the chart is definitionally an aggregate: an area mark (polygon/rect/heat),
///   or a fact cube layered over relatively few shapes. Coarse levels are honest means of children,
///   never samples.</item>
/// <item><b>QuadtreeLod</b> — a genuine point cloud (≈one row per shape, more shapes than fit): a
///   spatial pyramid whose coarse levels are a labeled, resolvable preview.</item>
/// </list></summary>
public static class ReductionPlanner
{
    // Above this many rows per distinct shape, the source is a fact cube over few shapes (e.g. a
    // time × dimension series per cell), which only makes sense to render as an aggregate.
    private const double CubeRowsPerShape = 4.0;

    public static ReductionKind Select(SourceBounds probe, ViewConfig view, int tilePointBudget)
    {
        if (probe.Count <= tilePointBudget)
            return ReductionKind.RawPassthrough;

        bool areaMark = view.Mark is Mark.Polygon or Mark.Rect or Mark.Heat;
        double rowsPerShape = probe.DistinctGeometries > 0
            ? (double)probe.Count / probe.DistinctGeometries
            : 1.0;

        if (areaMark || rowsPerShape >= CubeRowsPerShape)
            return ReductionKind.Aggregate;

        return ReductionKind.QuadtreeLod;
    }
}
