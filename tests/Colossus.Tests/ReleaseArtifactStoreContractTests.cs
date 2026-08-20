using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Baking;
using Xunit;

namespace Colossus.Tests;

public sealed class ReleaseArtifactStoreContractTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("colossus-artifact-contract-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public async Task FilesystemAndS3CompatibleStores_ObeyTheSamePublicationContract()
    {
        var fileWork = new FileBakeStore(
            Path.Combine(_root.FullName, "file-tiles"),
            Path.Combine(_root.FullName, "file-staging"));
        var objectWork = new FileBakeStore(
            Path.Combine(_root.FullName, "unused-tiles"),
            Path.Combine(_root.FullName, "object-staging"));
        var objects = new MemoryObjectClient();
        IReleaseArtifactStore objectStore = new S3ReleaseArtifactStore(objects, "fixture-bucket", "tiles");

        var fileResult = await ExerciseAsync(fileWork, fileWork, "file");
        var objectResult = await ExerciseAsync(objectWork, objectStore, "object");

        Assert.Equal(fileResult.Current, objectResult.Current);
        Assert.Equal(fileResult.SecondDeleted, objectResult.SecondDeleted);
        Assert.Equal(fileResult.CurrentProtected, objectResult.CurrentProtected);
        Assert.EndsWith("/contract-map/v1", objectStore.Location("contract-map", "v1"));
        Assert.Equal("tiles/contract-map/v1/manifest.json", objects.PutOrder
            .Where(key => key.StartsWith("tiles/contract-map/v1/", StringComparison.Ordinal)).Last());
        Assert.Equal("public, max-age=60", objects.CacheControl["tiles/contract-map/latest.json"]);
        Assert.All(objects.CacheControl
            .Where(value => value.Key.StartsWith("tiles/contract-map/v1/", StringComparison.Ordinal)),
            value => Assert.Equal("public, max-age=31536000, immutable", value.Value));
    }

    private static async Task<(string Current, bool SecondDeleted, bool CurrentProtected)> ExerciseAsync(
        FileBakeStore work,
        IReleaseArtifactStore artifacts,
        string token)
    {
        var first = await PrepareAsync(work, "v1", $"{token}-a1");
        Assert.False(artifacts.TryReadCurrent("contract-map", out _));
        artifacts.MakeAddressable(first);
        artifacts.MakeAddressable(first);
        Assert.True(artifacts.Exists("contract-map", "v1"));
        Assert.False(artifacts.TryReadCurrent("contract-map", out _));
        artifacts.PublishCurrent("contract-map", "v1");

        var second = await PrepareAsync(work, "v2", $"{token}-a2");
        artifacts.MakeAddressable(second);
        Assert.True(artifacts.TryReadCurrent("contract-map", out string beforeDelete));
        Assert.Equal("v1", beforeDelete);
        artifacts.Delete("contract-map", "v2");
        bool secondDeleted = !artifacts.Exists("contract-map", "v2");
        bool currentProtected = Assert.Throws<InvalidOperationException>(() =>
            artifacts.Delete("contract-map", "v1")) is not null;
        Assert.True(artifacts.TryReadCurrent("contract-map", out string current));
        return (current, secondDeleted, currentProtected);
    }

    private static async Task<ReleaseArtifactUpload> PrepareAsync(
        FileBakeStore work, string version, string attempt)
    {
        await work.WriteManifestAsync(Manifest(version), attempt);
        string directory = work.VersionDirectory("contract-map", version, attempt);
        string tile = Path.Combine(directory, "0", "0", "0.arrow");
        Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
        await File.WriteAllBytesAsync(tile, [1, 2, 3, 4]);
        return new ReleaseArtifactUpload(
            "contract-map", version, attempt, directory,
            work.PreparedManifestHash("contract-map", version, attempt));
    }

    private static Manifest Manifest(string version) => new()
    {
        Version = version,
        View = new ViewConfig
        {
            Id = "contract-map",
            Viewport = Viewport.Geo,
            Mark = Mark.Point,
            Source = new SourceSpec
            {
                Query = "SELECT x, y FROM fixture",
                Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            },
        },
        Reduction = ReductionKind.RawPassthrough,
        Regime = "large",
        Root = new Bbox(0, 0, 1, 1),
        MinZoom = 0,
        MaxZoom = 0,
        TilePointBudget = 10,
        TotalPoints = 1,
        SourceRows = 1,
        Tiles = [new TileMeta(0, 0, 0, 1, true)],
    };

    private sealed class MemoryObjectClient : IS3ObjectClient
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        public List<string> PutOrder { get; } = [];
        public Dictionary<string, string> CacheControl { get; } = new(StringComparer.Ordinal);

        public Task PutFileAsync(string key, string path, string contentType, string cacheControl)
        {
            _objects[key] = File.ReadAllBytes(path);
            PutOrder.Add(key);
            CacheControl[key] = cacheControl;
            return Task.CompletedTask;
        }

        public Task PutTextAsync(string key, string content, string contentType, string cacheControl)
        {
            _objects[key] = System.Text.Encoding.UTF8.GetBytes(content);
            PutOrder.Add(key);
            CacheControl[key] = cacheControl;
            return Task.CompletedTask;
        }

        public Task<byte[]?> ReadAsync(string key) =>
            Task.FromResult(_objects.TryGetValue(key, out byte[]? value) ? value.ToArray() : null);

        public Task<bool> ExistsAsync(string key) => Task.FromResult(_objects.ContainsKey(key));

        public Task<IReadOnlyList<string>> ListAsync(string prefix) =>
            Task.FromResult<IReadOnlyList<string>>([.. _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))]);

        public Task DeleteAsync(IReadOnlyList<string> keys)
        {
            foreach (string key in keys) _objects.Remove(key);
            return Task.CompletedTask;
        }
    }
}
