using Colossus.Application;
using Colossus.Application.Abstractions.Baking;
using Colossus.Application.Abstractions.Reduction;
using Colossus.Application.Abstractions.Sources;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.Sources;
using Xunit;

namespace Colossus.Tests;

public sealed class BakeViewUseCaseTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("colossus-bake-");
    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Bake_OrchestratesSourceReductionManifestAndAtomicPublication()
    {
        var source = new RecordingSource();
        var reduction = new RecordingReduction();
        var store = new FileBakeStore(
            Path.Combine(_dir.FullName, "tiles"),
            Path.Combine(_dir.FullName, "staging"));
        var progress = new ProgressRecorder();
        var useCase = new BakeViewUseCase(
            new SourceAdapterCatalog([source]),
            new OneReductionCatalog(reduction),
            store,
            new BakePlanner(tilePointBudget: 10),
            new EmptyDomains(),
            new UnexpectedGrouper(),
            new FoldRoutingOptions(),
            new NoCompression(),
            new NoPacking(),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 34, 56, TimeSpan.Zero)));

        var outcome = await useCase.BakeAsync(View(), progress);

        Assert.Equal("v20260810T123456Z", outcome.Version);
        Assert.Equal(store.StagingPath("characterized-map"), source.ExtractDestination);
        Assert.Equal(store.VersionDirectory("characterized-map", outcome.Version!), reduction.Context!.OutputDirectory);
        Assert.True(store.TryReadLatestVersion("characterized-map", out var current));
        Assert.Equal(outcome.Version, current);

        var manifest = await store.ReadManifestAsync("characterized-map", current);
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.SourceRows);
        Assert.Equal(3, manifest.TotalPoints);
        Assert.Equal(ReductionKind.RawPassthrough, manifest.Reduction);
        Assert.Equal([BakeStage.Probing, BakeStage.Extracting, BakeStage.Extracting, BakeStage.Tiling, BakeStage.Verifying, BakeStage.Publishing, BakeStage.Completed],
            progress.Events.Select(e => e.Stage));
        Assert.Contains(progress.Events, item => item.Message.Contains("2.0 MB received"));
    }

    private static ViewConfig View() => new()
    {
        Id = "characterized-map",
        Viewport = Viewport.Geo,
        Mark = Mark.Point,
        Source = new SourceSpec
        {
            Adapter = "characterized",
            Query = "SELECT x, y, value",
            Geometry = new GeometrySpec { Kind = GeometryKind.Xy, X = "x", Y = "y" },
            Channels =
            [
                new ChannelSpec { Name = "value", Column = "value", Role = ChannelRole.Measure, Type = ChannelType.F32 },
            ],
        },
        Encoding = new EncodingSpec { Color = new ColorSpec { Channel = "value" } },
    };

    private sealed class RecordingSource : ISourceAdapter
    {
        public SourceAdapterDescriptor Descriptor { get; } = new(
            "characterized", "Characterized", new(false, false, false, false, true), [], []);
        public string? ExtractDestination { get; private set; }

        public Task<SourceConnectionValidation> ValidateConnectionAsync(
            SourceConnectionContext connection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SourceLocation>> DiscoverAsync(
            SourceConnectionContext connection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SourcePreview> PreviewAsync(
            SourcePreviewRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default) =>
            Task.FromResult(new SourceBounds(new Bbox(0, 0, 1, 1), Count: 3, DistinctGeometries: 3));

        public Task ExtractAsync(ViewConfig view, Bbox bounds, string destinationParquet,
            IProgress<SourceExtractProgress>? progress = null,
            CancellationToken ct = default)
        {
            ExtractDestination = destinationParquet;
            progress?.Report(new SourceExtractProgress(2 * 1024 * 1024));
            progress?.Report(new SourceExtractProgress(2 * 1024 * 1024, Completed: true));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReduction : IReductionStrategy
    {
        public ReductionKind Kind => ReductionKind.RawPassthrough;
        public ReductionContext? Context { get; private set; }

        public ReductionResult Reduce(ReductionContext context)
        {
            Context = context;
            return new ReductionResult([new TileMeta(0, 0, 0, Count: 3, IsLeaf: true)], LeafPointCount: 3);
        }
    }

    private sealed class OneReductionCatalog(IReductionStrategy reduction) : IReductionCatalog
    {
        public IReductionStrategy Resolve(ReductionKind kind)
        {
            Assert.Equal(reduction.Kind, kind);
            return reduction;
        }
    }

    private sealed class EmptyDomains : IChannelDomainScanner
    {
        public IReadOnlyDictionary<string, ChannelDomain> Scan(string stagingParquetPath, ViewConfig view) =>
            new Dictionary<string, ChannelDomain>();
    }

    private sealed class UnexpectedGrouper : IFactGrouper
    {
        public FactGrouping GroupToMarks(string factsParquetPath, string marksParquetPath, ViewConfig view) =>
            throw new Xunit.Sdk.XunitException("Row-regime bake must not invoke the fact grouper.");
    }

    private sealed class NoCompression : ITileCompressor
    {
        public TileCompressionStats CompressVersionTiles(string versionDirectory) => TileCompressionStats.None;
    }

    private sealed class NoPacking : ITilePacker
    {
        public RenderPack? PackVersion(string versionDirectory, IReadOnlyList<TileMeta> tiles,
            IReadOnlyList<string> firstPaintChannels) => null;
    }

    private sealed class ProgressRecorder : IProgress<BakeProgress>
    {
        public List<BakeProgress> Events { get; } = [];
        public void Report(BakeProgress value) => Events.Add(value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
