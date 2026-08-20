using System.Text.Json;
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

    [Fact]
    public void EncodingColor_FullAuthoredSpecAndPartialOverrides_Passes()
    {
        var view = Valid() with
        {
            Encoding = new EncodingSpec
            {
                Color = new ColorSpec
                {
                    Channel = "v",
                    Type = "diverging",
                    Scheme = "blueRed",
                    Range = ["#2166ac", "#f7f7f7", "#b35806"],
                    Domain = [JsonSerializer.SerializeToElement(-100), JsonSerializer.SerializeToElement(100)],
                    Reverse = true,
                    Midpoint = 0,
                    Unknown = "#888888",
                },
            },
        };

        view.Validate();
        (view with
        {
            Encoding = new EncodingSpec
            {
                Color = new ColorSpec
                {
                    Channel = "v", Type = "categorical", Scheme = "okabeIto",
                    Domain = [JsonSerializer.SerializeToElement("A"), JsonSerializer.SerializeToElement("B")],
                    Overrides = new Dictionary<string, string> { ["B"] = "#123456" },
                },
            },
        }).Validate();
    }

    [Fact]
    public void EncodingColor_RejectsMalformedAdvancedSettings()
    {
        ColorSpec[] invalid =
        [
            new() { Channel = "v", Type = "rainbow" },
            new() { Channel = "v", Type = "linear", Range = ["#fff"] },
            new() { Channel = "v", Type = "linear", Range = ["#fff", "not-a-color"] },
            new() { Channel = "v", Type = "quantize", Bins = 13 },
            new() { Channel = "v", Type = "linear", Bins = 5 },
            new() { Channel = "v", Type = "linear", Midpoint = 0 },
            new() { Channel = "v", Type = "threshold", Thresholds = [10, 5] },
            new() { Channel = "v", Type = "linear", Domain = [JsonSerializer.SerializeToElement(10), JsonSerializer.SerializeToElement(5)] },
            new() { Channel = "v", Type = "log", Domain = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(5)] },
            new() { Channel = "v", Type = "categorical", Palette = new Dictionary<string, string> { ["A"] = "#fff" }, Overrides = new Dictionary<string, string> { ["B"] = "#000" } },
            new() { Channel = "v", Unknown = "transparent" },
        ];

        foreach (ColorSpec color in invalid)
        {
            var view = Valid() with { Encoding = new EncodingSpec { Color = color } };
            Assert.Contains("encoding.color", Assert.Throws<ArgumentException>(view.Validate).Message);
        }
    }

    [Fact]
    public void Filters_RequireUniqueChannelsAndCompatibleControls()
    {
        (ValidGroup() with
        {
            Filters =
            [
                new FilterSpec { Channel = "operator", Control = ControlKind.Select },
                new FilterSpec { Channel = "quarter", Control = ControlKind.DateRange },
            ],
        }).Validate();

        (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "tests", Control = ControlKind.Range }],
        }).Validate();
        Assert.Throws<ArgumentException>(() => (ValidGroup() with
        {
            Filters =
            [
                new FilterSpec { Channel = "operator", Control = ControlKind.Select },
                new FilterSpec { Channel = "operator", Control = ControlKind.MultiSelect },
            ],
        }).Validate());

        Assert.Throws<ArgumentException>(() => (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "tests", Control = ControlKind.Select }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "operator", Control = ControlKind.Range }],
        }).Validate());
        Assert.Throws<ArgumentException>(() => (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "tests", Control = ControlKind.DateRange }],
        }).Validate());
    }

    [Theory]
    [InlineData("10..20")]
    [InlineData("10..")]
    [InlineData("..20")]
    [InlineData("12.5")]
    [InlineData("-1.25..2e3")]
    public void NumericRange_DefaultAcceptsInclusiveOpenOrExactBounds(string value) =>
        (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "tests", Control = ControlKind.Range, Default = value }],
        }).Validate();

    [Theory]
    [InlineData("..")]
    [InlineData("20..10")]
    [InlineData("nope..20")]
    [InlineData("NaN..20")]
    [InlineData("1..Infinity")]
    public void NumericRange_DefaultRejectsInvalidBounds(string value) =>
        Assert.Throws<ArgumentException>(() => (ValidGroup() with
        {
            Filters = [new FilterSpec { Channel = "tests", Control = ControlKind.Range, Default = value }],
        }).Validate());

    [Fact]
    public void BakeFilters_RejectEmptyPredicates() =>
        Assert.Throws<ArgumentException>(() => (Valid() with { BakeFilters = [" "] }).Validate());

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
    public void Measures_UseImplicitIdentityForEverySupportedGeometryKind()
    {
        GeometrySpec[] geometries =
        [
            new() { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            new() { Kind = GeometryKind.LonLat, Lon = "lon", Lat = "lat" },
            new() { Kind = GeometryKind.Quadkey, Column = "quadkey" },
            new() { Kind = GeometryKind.Wkt, Column = "wkt" },
        ];

        foreach (var geometry in geometries)
        {
            var view = ValidGroup(("m", "count()")) with
            {
                Source = ValidGroup(("m", "count()")).Source with { Geometry = geometry },
            };
            view.Validate();
        }
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
