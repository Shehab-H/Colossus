using Colossus.Application;
using Colossus.Domain.Model;
using Colossus.Domain.Sources;
using Xunit;

namespace Colossus.Tests;

public class BakePlannerTests
{
    private const int Budget = 250_000;
    private static readonly BakePlanner Planner = new(Budget);

    private static ViewConfig View(Mark mark) => new()
    {
        Id = "test-view",
        Viewport = Viewport.Geo,
        Mark = mark,
        Source = new SourceSpec
        {
            Query = "SELECT 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
        },
    };

    private static SourceBounds Probe(long count, long shapes) => new(new Bbox(0, 0, 1, 1), count, shapes);

    [Fact]
    public void SourceUnderBudget_ShipsRaw() =>
        Assert.Equal(ReductionKind.RawPassthrough, Planner.Plan(Probe(Budget, Budget), View(Mark.Point)).Reduction);

    [Fact]
    public void AreaMarkOverBudget_Aggregates() =>
        Assert.Equal(ReductionKind.Aggregate, Planner.Plan(Probe(10_000_000, 10_000_000), View(Mark.Polygon)).Reduction);

    [Fact]
    public void FactCubeOverFewShapes_Aggregates() =>
        // 300M rows over 3M distinct shapes = 100 rows/shape — a cube, even with a point mark.
        Assert.Equal(ReductionKind.Aggregate, Planner.Plan(Probe(300_000_000, 3_000_000), View(Mark.Point)).Reduction);

    [Fact]
    public void GenuinePointCloud_GetsQuadtreeLod() =>
        Assert.Equal(ReductionKind.QuadtreeLod, Planner.Plan(Probe(20_000_000, 19_500_000), View(Mark.Point)).Reduction);

    [Fact]
    public void Plan_PadsRootToASquareContainingTheProbeBounds()
    {
        var plan = Planner.Plan(new SourceBounds(new Bbox(0, 0, 10, 4), 1000, 1000), View(Mark.Point));
        Assert.Equal(plan.Root.SpanX, plan.Root.SpanY, precision: 9);
        Assert.True(plan.Root.MinX <= 0 && plan.Root.MaxX >= 10);
        Assert.True(plan.Root.MinY <= 0 && plan.Root.MaxY >= 4);
    }

    [Theory]
    [InlineData(1, 5)] // few shapes → the generous floor (5)
    [InlineData(250_000, 5)] // ~one budget of shapes → still shallow
    [InlineData(300_000_000, 11)] // 300M shapes → deeper
    [InlineData(500_000_000_000, 16)] // absurdly many → capped at 16
    public void PlanMaxZoom_ScalesWithShapes_AndStaysInRange(long shapes, int expected)
    {
        int z = Planner.Plan(Probe(shapes + 1, shapes), View(Mark.Point)).MaxZoom;
        Assert.Equal(expected, z);
        Assert.InRange(z, 1, 16);
    }

    [Fact]
    public void Plan_CarriesTheConfiguredBudget() =>
        Assert.Equal(Budget, Planner.Plan(Probe(1000, 1000), View(Mark.Point)).TilePointBudget);
}
