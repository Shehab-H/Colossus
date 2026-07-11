using Colossus.Application;
using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Xunit;

namespace Colossus.Tests;

public class EffectiveViewTests
{
    private static ViewConfig Authored => new()
    {
        Id = "mobile-dominance",
        Viewport = Viewport.Geo,
        Mark = Mark.Polygon,
        Source = new SourceSpec
        {
            Query = "SELECT 1",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = new[]
            {
                new ChannelSpec { Name = "operator", Column = "operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "region", Column = "region", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "tests", Column = "tests", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            },
        },
        Measures = new[]
        {
            new MeasureSpec { Name = "total_tests", Expr = "sum(tests)" },
            new MeasureSpec { Name = "dominant_operator", Expr = "argmax(operator, sum(tests))" },
        },
        Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "dominant_operator", Type = "categorical" } },
    };

    [Fact]
    public void For_MaterializesMarksColumns_IdPerMarkAndMeasures()
    {
        // region perMark; operator perFact (dropped from marks — it lives only in companions).
        var eff = EffectiveView.For(Authored, new FactGrouping(
            PerMarkChannels: new[] { "region" },
            PerFactChannels: new[] { "operator", "tests" }));

        Assert.Null(eff.Measures);                        // materialized: no longer a group source
        var byName = eff.Source.Channels.ToDictionary(c => c.Name);

        Assert.Equal(new[] { "id", "region", "total_tests", "dominant_operator" }, eff.Source.Channels.Select(c => c.Name));
        Assert.Equal((ChannelRole.Identity, ChannelType.Dict), (byName["id"].Role, byName["id"].Type));
        Assert.Equal((ChannelRole.Dimension, ChannelType.Dict), (byName["region"].Role, byName["region"].Type));
        Assert.Equal((ChannelRole.Measure, ChannelType.F32), (byName["total_tests"].Role, byName["total_tests"].Type));
        Assert.Equal((ChannelRole.Dimension, ChannelType.Dict), (byName["dominant_operator"].Role, byName["dominant_operator"].Type));
        Assert.DoesNotContain("operator", eff.Source.Channels.Select(c => c.Name)); // perFact gone

        // The argmax measure and the mark id are dict-encoded exactly as any dimension; id (identity) is not.
        Assert.Equal(new HashSet<string> { "region", "dominant_operator" }, eff.DictionaryEncodedChannels());
    }
}
