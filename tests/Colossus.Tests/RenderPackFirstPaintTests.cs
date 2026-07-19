using Colossus.Domain.Model;
using Xunit;

namespace Colossus.Tests;

/// <summary>The first-paint set must match the client's predicateChannels exactly. A filter-slot column the
/// client cannot find reads as MISSING_CODE — matched by nothing — so an omission does not fail loudly, it
/// silently blanks the view on the first filter selection.</summary>
public class RenderPackFirstPaintTests
{
    private static ChannelSpec Ch(string name, ChannelRole role) =>
        new() { Name = name, Column = name, Role = role, Type = role == ChannelRole.Measure ? ChannelType.F32 : ChannelType.Dict };

    private static ViewConfig View(params ChannelSpec[] channels) => new()
    {
        Id = "v",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Adapter = "test",
            Query = "select 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat },
            Channels = channels,
        },
        Encoding = new EncodingSpec { Color = new ColorSpec { Channel = channels[0].Name, Type = "categorical" } },
    };

    [Fact]
    public void Slots_come_from_roles_not_the_authored_filters_list()
    {
        // geonames' shape: filters is null, yet dimension/temporal channels are live GPU filter slots.
        var view = View(
            Ch("feature_class", ChannelRole.Dimension),
            Ch("feature_code", ChannelRole.Dimension),
            Ch("elevation", ChannelRole.Measure),
            Ch("modification_date", ChannelRole.Temporal),
            Ch("name", ChannelRole.Identity));
        Assert.Null(view.Filters);

        var fp = RenderPack.FirstPaintChannels(view, ReductionKind.QuadtreeLod);

        Assert.Equal(["feature_class", "feature_code", "modification_date"], fp);
        Assert.DoesNotContain("elevation", fp); // a measure is lazy
        Assert.DoesNotContain("name", fp);      // identity is inspect-only
    }

    [Fact]
    public void Aggregate_row_regime_carries_no_slots()
    {
        // The aggregate reducer drops dimension/temporal columns, so offering them would blank the view.
        var view = View(Ch("value", ChannelRole.Measure), Ch("cat", ChannelRole.Dimension));
        Assert.Equal(["value"], RenderPack.FirstPaintChannels(view, ReductionKind.Aggregate));
    }

    [Fact]
    public void Group_regime_excludes_perFact_channels_from_slots()
    {
        // A perFact selection is fold context, never a GPU predicate — those columns stay lazy.
        var view = View(Ch("op", ChannelRole.Dimension), Ch("quarter", ChannelRole.Temporal)) with
        {
            Measures = [new MeasureSpec { Name = "m", Expr = "sum(x)" }],
        };
        var fp = RenderPack.FirstPaintChannels(view, ReductionKind.Aggregate, ["quarter"]);

        Assert.Contains("op", fp);
        Assert.DoesNotContain("quarter", fp);
    }

    [Fact]
    public void Never_exceeds_the_four_gpu_slots()
    {
        var view = View(
            Ch("a", ChannelRole.Dimension), Ch("b", ChannelRole.Dimension), Ch("c", ChannelRole.Dimension),
            Ch("d", ChannelRole.Dimension), Ch("e", ChannelRole.Dimension), Ch("f", ChannelRole.Dimension));
        // colour channel 'a' doubles as a slot, so the run is a..d — the client builds no 5th slot.
        Assert.Equal(["a", "b", "c", "d"], RenderPack.FirstPaintChannels(view, ReductionKind.QuadtreeLod));
    }
}
