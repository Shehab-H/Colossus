using Colossus.Domain.Model;
using Colossus.Infrastructure.Baking;
using Xunit;

namespace Colossus.Tests;

public class FileBakeStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-store-");
    private readonly FileBakeStore _store;

    public FileBakeStoreTests() =>
        _store = new FileBakeStore(Path.Combine(_dir.FullName, "tiles"), Path.Combine(_dir.FullName, "staging"));

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task PublishFlow_RoundTrips()
    {
        var manifest = new Manifest
        {
            Version = "v20260101T000000Z",
            View = new ViewConfig
            {
                Id = "test-view",
                Viewport = Viewport.Geo,
                Mark = Mark.Point,
                Source = new SourceSpec
                {
                    Query = "SELECT 1",
                    Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
                },
            },
            Reduction = ReductionKind.RawPassthrough,
            Regime = "large",
            Root = new Bbox(0, 0, 1, 1),
            MinZoom = 0,
            MaxZoom = 0,
            TilePointBudget = 1000,
            TotalPoints = 7,
            Tiles = [new TileMeta(0, 0, 0, 7, IsLeaf: true)],
        };

        Assert.False(_store.TryReadLatestVersion("test-view", out _));

        await _store.WriteManifestAsync(manifest);
        _store.PublishLatest("test-view", manifest.Version);

        Assert.True(_store.TryReadLatestVersion("test-view", out var version));
        Assert.Equal(manifest.Version, version);

        var back = await _store.ReadManifestAsync("test-view", version);
        Assert.NotNull(back);
        Assert.Equal(7, back!.TotalPoints);

        string tilePath = _store.TilePath("test-view", version, new TileId(0, 0, 0));
        Assert.EndsWith(Path.Combine("test-view", version, new TileId(0, 0, 0).RelativePath), tilePath);
    }

    [Fact]
    public async Task ReadManifest_MissingVersion_ReturnsNull() =>
        Assert.Null(await _store.ReadManifestAsync("test-view", "v-nope"));
}
