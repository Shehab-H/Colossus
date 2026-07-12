using Colossus.Domain.Model;
using Xunit;

namespace Colossus.Tests;

public class ViewConfigTests
{
    private static ViewConfig Valid(string id = "geo-points") => new()
    {
        Id = id,
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Query = "SELECT lon, lat, v FROM db.t",
            Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            Channels = new[] { new ChannelSpec { Name = "v", Column = "v", Role = ChannelRole.Measure, Type = ChannelType.F32 } },
        },
    };

    [Fact]
    public void ValidConfig_Passes() => Valid().Validate();

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("Upper-Case")]
    [InlineData("spaced id")]
    public void UnsafeOrEmptyId_Throws(string id) =>
        Assert.Throws<ArgumentException>(() => Valid(id).Validate());

    [Theory]
    [InlineData("ookla-fixed")]
    [InlineData("crowd-download-2024")]
    public void KebabCaseId_IsValid(string id) => Assert.True(ViewConfig.IsValidId(id));

    [Fact]
    public void MissingQuery_Throws()
    {
        var view = Valid() with { Source = Valid().Source with { Query = " " } };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void LonLatGeometry_RequiresBothColumns()
    {
        var view = Valid() with
        {
            Source = Valid().Source with { Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon" } },
        };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void ColumnGeometry_RequiresTheColumn()
    {
        var view = Valid() with
        {
            Source = Valid().Source with { Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey } },
        };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void EncodingColor_KnownChannel_Passes() =>
        (Valid() with { Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "v", Scheme = "plasma" } } }).Validate();

    [Fact]
    public void EncodingColor_CategoricalSpec_Passes() =>
        (Valid() with
        {
            Encoding = new EncodingSpec
            {
                Color = new ColorSpec
                {
                    Channel = "v",
                    Type = "categorical",
                    Palette = new Dictionary<string, string> { ["a"] = "#ff0000" },
                    Unknown = "#888888",
                },
            },
        }).Validate();

    [Fact]
    public void EncodingColor_UnknownChannel_Throws()
    {
        var view = Valid() with { Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "nope" } } };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void EncodingColor_NonPositiveBins_Throws()
    {
        var view = Valid() with { Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "v", Type = "quantize", Bins = 0 } } };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void Inspect_KnownChannels_Passes() =>
        (Valid() with { Inspect = new InspectSpec { Title = "v", Channels = new[] { "v" } } }).Validate();

    [Fact]
    public void Inspect_UnknownChannel_Throws()
    {
        var view = Valid() with { Inspect = new InspectSpec { Channels = new[] { "ghost" } } };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void Inspect_EmptyChannels_Throws()
    {
        var view = Valid() with { Inspect = new InspectSpec { Channels = Array.Empty<string>() } };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    // ── Group regime (measures) — VIEW_CONFIG §4/§11 config-time validation ──

    private static ViewConfig ValidGroup(params (string name, string expr)[] measures) => new()
    {
        Id = "mobile-dominance",
        Viewport = Viewport.Geo,
        Mark = Mark.Polygon,
        Source = new SourceSpec
        {
            Query = "SELECT quadkey, quarter, operator, tests, download_mbps FROM db.t",
            Geometry = new GeometrySpec { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            Channels = new[]
            {
                new ChannelSpec { Name = "operator", Column = "operator", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                new ChannelSpec { Name = "quarter", Column = "quarter", Role = ChannelRole.Temporal, Type = ChannelType.Date },
                new ChannelSpec { Name = "tests", Column = "tests", Role = ChannelRole.Measure, Type = ChannelType.F32 },
                new ChannelSpec { Name = "download_mbps", Column = "download_mbps", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            },
        },
        Measures = measures.Select(m => new MeasureSpec { Name = m.name, Expr = m.expr }).ToArray(),
    };

    [Fact]
    public void GroupRegime_FlagshipMeasures_Pass() => (ValidGroup(
        ("total_tests", "sum(tests)"),
        ("avg_download", "wavg(download_mbps, tests)"),
        ("apex_share", "share(sum(tests)) where operator = 'apex'"),
        ("dominant_operator", "argmax(operator, sum(tests))")) with
    {
        Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "dominant_operator", Type = "categorical" } },
        Inspect = new InspectSpec { Title = "dominant_operator", Channels = new[] { "dominant_operator", "total_tests" } },
    }).Validate();

    [Fact]
    public void HasMeasures_TrueOnlyWithMeasures()
    {
        Assert.True(ValidGroup(("m", "count()")).HasMeasures);
        Assert.False(Valid().HasMeasures);
    }

    [Fact]
    public void Measure_NameCollidesWithChannel_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("tests", "sum(tests)")).Validate());

    [Fact]
    public void Measure_DuplicateName_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("m", "sum(tests)"), ("m", "count()")).Validate());

    [Fact]
    public void Measure_BadGrammar_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("m", "sum(")).Validate());

    [Fact]
    public void Measure_VerbArgNotNumeric_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("m", "sum(operator)")).Validate());

    [Fact]
    public void Measure_ArgmaxDimNotDict_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("m", "argmax(tests, sum(tests))")).Validate());

    [Fact]
    public void Measure_WhereChannelNotDict_Throws() =>
        Assert.Throws<ArgumentException>(() => ValidGroup(("m", "share(sum(tests)) where tests = '1'")).Validate());

    [Fact]
    public void Measure_NonQuadkeyGeometry_Throws()
    {
        var view = ValidGroup(("m", "count()")) with
        {
            Source = ValidGroup(("m", "count()")).Source with
            {
                Geometry = new GeometrySpec { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            },
        };
        Assert.Throws<ArgumentException>(view.Validate);
    }

    [Fact]
    public void Measure_ColorByUnknownName_Throws() =>
        Assert.Throws<ArgumentException>(() => (ValidGroup(("m", "count()")) with
        {
            Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "ghost" } },
        }).Validate());

    [Fact]
    public void DictionaryEncodedChannels_IsDictTypeMinusIdentityRole()
    {
        var view = Valid() with
        {
            Source = Valid().Source with
            {
                Channels = new[]
                {
                    new ChannelSpec { Name = "m", Column = "m", Role = ChannelRole.Measure, Type = ChannelType.F32 },
                    new ChannelSpec { Name = "cat", Column = "cat", Role = ChannelRole.Dimension, Type = ChannelType.Dict },
                    new ChannelSpec { Name = "day", Column = "day", Role = ChannelRole.Temporal, Type = ChannelType.Date },
                    new ChannelSpec { Name = "name", Column = "name", Role = ChannelRole.Identity, Type = ChannelType.Dict },
                },
            },
        };
        Assert.Equal(new HashSet<string> { "cat" }, view.DictionaryEncodedChannels());
    }
}
