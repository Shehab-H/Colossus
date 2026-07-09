using Colossus.Domain.Model;
using Colossus.Infrastructure.Serialization;
using Xunit;

namespace Colossus.Tests;

public class ColossusJsonTests
{
    [Fact]
    public void AuthoredViewConfig_Deserializes_WithCamelCaseEnums()
    {
        var view = ColossusJson.Deserialize<ViewConfig>("""
            {
              "id": "geo-points",
              "viewport": "geo",
              "mark": "point",
              "reduction": "quadtreeLod",
              "source": {
                "adapter": "clickhouse",
                "query": "SELECT lon, lat, value FROM colossus.points_geo",
                "geometry": { "kind": "lonLat", "lon": "lon", "lat": "lat" },
                "channels": [
                  { "name": "value", "column": "value", "role": "measure", "type": "f32" }
                ]
              }
            }
            """);

        view.Validate();
        Assert.Equal(Viewport.Geo, view.Viewport);
        Assert.Equal(Mark.Point, view.Mark);
        Assert.Equal(ReductionKind.QuadtreeLod, view.Reduction);
        Assert.Equal(GeometryKind.LonLat, view.Source.Geometry.Kind);
        Assert.Equal(ChannelType.F32, Assert.Single(view.Source.Channels).Type);
    }

    [Fact]
    public void Manifest_DoesNotSerializeDerivedProperties()
    {
        var manifest = new Manifest
        {
            Version = "v1",
            View = new ViewConfig
            {
                Id = "t",
                Viewport = Viewport.Geo,
                Mark = Mark.Point,
                Source = new SourceSpec
                {
                    Query = "SELECT 1",
                    Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
                },
            },
            Reduction = ReductionKind.QuadtreeLod,
            Regime = "large",
            Root = new Bbox(0, 0, 1, 1),
            MinZoom = 0,
            MaxZoom = 3,
            TilePointBudget = 1000,
            TotalPoints = 42,
            Tiles = [new TileMeta(0, 0, 0, 42, IsLeaf: true)],
        };

        string json = ColossusJson.Serialize(manifest);
        // TileMeta.Id and Bbox spans are derived; serializing them doubled the manifest size.
        Assert.DoesNotContain("relativePath", json);
        Assert.DoesNotContain("spanX", json);
        Assert.Contains("\"isLeaf\": true", json);

        var back = ColossusJson.Deserialize<Manifest>(json);
        Assert.Equal(manifest.Version, back.Version);
        Assert.Equal(42, Assert.Single(back.Tiles).Count);
    }
}
