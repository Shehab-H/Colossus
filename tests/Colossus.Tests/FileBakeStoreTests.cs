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

    [Fact]
    public async Task PreparedRelease_IsPrivateUntilAddressableAndPointerFlipIsFinal()
    {
        var previous = ManifestFor("v-old");
        await _store.WriteManifestAsync(previous);
        _store.PublishLatest("test-view", previous.Version);

        const string attempt = "run-1-a1";
        var next = ManifestFor("v-new");
        await _store.WriteManifestAsync(next, attempt);

        Assert.Null(await _store.ReadManifestAsync("test-view", next.Version));
        Assert.True(_store.TryReadLatestVersion("test-view", out string before));
        Assert.Equal(previous.Version, before);
        Assert.Equal(64, _store.PreparedManifestHash("test-view", next.Version, attempt).Length);

        _store.MakeVersionAddressable("test-view", next.Version, attempt);

        Assert.NotNull(await _store.ReadManifestAsync("test-view", next.Version));
        Assert.True(_store.TryReadLatestVersion("test-view", out string addressable));
        Assert.Equal(previous.Version, addressable);

        _store.PublishLatest("test-view", next.Version);
        _store.MakeVersionAddressable("test-view", next.Version, attempt);
        _store.PublishLatest("test-view", next.Version);

        Assert.True(_store.TryReadLatestVersion("test-view", out string published));
        Assert.Equal(next.Version, published);
        Assert.Throws<InvalidOperationException>(() => _store.DeleteVersion("test-view", next.Version));
    }

    [Fact]
    public async Task InvalidPreparationAndConcurrentAttempts_CannotReplaceCurrentOrShareWorkspace()
    {
        var previous = ManifestFor("v-current");
        await _store.WriteManifestAsync(previous);
        _store.PublishLatest("test-view", previous.Version);

        string first = _store.StagingPath("test-view", "run-a-a1");
        string second = _store.StagingPath("test-view", "run-b-a1");
        Assert.NotEqual(first, second);
        Assert.Contains("run-a-a1", first);
        Assert.Contains("run-b-a1", second);

        _ = _store.VersionDirectory("test-view", "v-invalid", "run-b-a1");
        Assert.Throws<InvalidDataException>(() =>
            _store.MakeVersionAddressable("test-view", "v-invalid", "run-b-a1"));
        Assert.True(_store.TryReadLatestVersion("test-view", out string current));
        Assert.Equal(previous.Version, current);
    }

    [Fact]
    public void DirectoryPromotion_CrossDeviceMoveCopiesThenAtomicallyRenamesOnDestination()
    {
        string source = Path.Combine(_dir.FullName, "cross-device-source");
        string destination = Path.Combine(_dir.FullName, "cross-device-destination");
        string nested = Path.Combine(source, "0", "0", "0.arrow");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, [1, 2, 3, 4]);

        FileBakeStore.PromoteDirectory(source, destination, (_, _) =>
            throw new IOException("Invalid cross-device link"));

        Assert.True(Directory.Exists(source));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(destination, "0", "0", "0.arrow")));
        Assert.Empty(Directory.EnumerateDirectories(_dir.FullName, "*.copy-*"));
    }

    private static Manifest ManifestFor(string version) => new()
    {
        Version = version,
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
}
