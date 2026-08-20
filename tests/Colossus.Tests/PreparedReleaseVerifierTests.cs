using Colossus.Application.Abstractions.Baking;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Baking;
using Xunit;

namespace Colossus.Tests;

public sealed class PreparedReleaseVerifierTests
{
    [Fact]
    public void Verify_AcceptsAnExactPrivateReleaseWitness()
    {
        var tiles = new TileFixture(new Dictionary<string, long>
        {
            [Path.Combine("0", "0", "0.arrow")] = 3,
        });
        var verifier = new PreparedReleaseVerifier(tiles, new StagingFixture(3));

        var result = verifier.Verify(Release(total: 3, manifestCount: 3));

        Assert.True(result.Passed, result.Message);
    }

    [Fact]
    public void Verify_RejectsTileAndExtractionMismatchesBeforePublication()
    {
        var wrongTile = new PreparedReleaseVerifier(
            new TileFixture(new Dictionary<string, long>
            {
                [Path.Combine("0", "0", "0.arrow")] = 2,
            }),
            new StagingFixture(3));
        var wrongExtract = new PreparedReleaseVerifier(
            new TileFixture(new Dictionary<string, long>
            {
                [Path.Combine("0", "0", "0.arrow")] = 3,
            }),
            new StagingFixture(2));

        var tileResult = wrongTile.Verify(Release(total: 3, manifestCount: 3));
        var extractResult = wrongExtract.Verify(Release(total: 3, manifestCount: 3));

        Assert.False(tileResult.Passed);
        Assert.Contains("has 2 rows", tileResult.Message);
        Assert.False(extractResult.Passed);
        Assert.Contains("staged extract has 2", extractResult.Message);
    }

    private static PreparedRelease Release(long total, long manifestCount)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "prepared-release-fixture"));
        return new PreparedRelease(new Manifest
        {
            Version = "v1",
            View = new ViewConfig
            {
                Id = "prepared-map",
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
            TotalPoints = total,
            SourceRows = total,
            Tiles = [new TileMeta(0, 0, 0, manifestCount, true)],
        }, root, "source.parquet", "marks.parquet");
    }

    private sealed class TileFixture(IReadOnlyDictionary<string, long> rows) : ITileReader
    {
        public long RowCount(string tilePath)
        {
            string relative = string.Join(Path.DirectorySeparatorChar,
                tilePath.Split(Path.DirectorySeparatorChar).TakeLast(3));
            return rows[relative];
        }

        public bool Exists(string tilePath) => true;
        public long PackedRowCount(string packPath, long offset, long length, string codec) =>
            throw new NotSupportedException();
        public long SlabFacts(string packPath, IReadOnlyDictionary<string, long[]> planes,
            CompanionSlab slab, string codec, byte[]? dict) => throw new NotSupportedException();
        public long RenderPackedRowCount(string packPath, long offset, long length, string codec, byte[]? dict) =>
            throw new NotSupportedException();
    }

    private sealed class StagingFixture(long rows) : IStagingReader
    {
        public bool Exists(string stagingPath) => stagingPath == "source.parquet";
        public long RowCount(string stagingPath) => rows;
    }
}
