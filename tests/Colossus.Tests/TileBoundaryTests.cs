using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Xunit;

namespace Colossus.Tests;

/// <summary>Pins the partition invariant: at any mix of depths, every point of the root box belongs to
/// exactly one leaf. A regression guard for the seam bug where a tile's max edge was computed as
/// <c>edgeMin + cell</c> — rounding twice — so it drifted by an ulp from the neighbour's min edge and let
/// 69 of GeoNames' 13.4M rows be counted in two leaves at once.</summary>
public class TileBoundaryTests
{
    /// <summary>GeoNames' baked root: a padded square whose span makes <c>span / 2^z</c> land on an
    /// inexact double at nearly every level — the bbox that exposed the bug.</summary>
    private static readonly Bbox Geonames = new(-180.17999999999998, -180.17999999999998,
                                                 180.17999999999998, 180.17999999999998);

    public static TheoryData<Bbox> Roots => new()
    {
        Geonames,
        new Bbox(0, 0, 1, 1),
        new Bbox(-10, -10, 10, 10),
        new Bbox(0.1, 0.1, 0.30000000000000004, 0.30000000000000004),
        new Bbox(-73.99, -73.99, 40.77, 40.77),
    };

    [Theory, MemberData(nameof(Roots))]
    public void MaxEdge_IsTheNeighboursMinEdge_BitForBit(Bbox root)
    {
        for (int z = 0; z <= 10; z++)
        {
            long n = 1L << z;
            for (long i = 0; i < n - 1; i++)
            {
                var left = TileMath.TileRect(root, new TileId(z, (int)i, 0));
                var right = TileMath.TileRect(root, new TileId(z, (int)(i + 1), 0));
                Assert.True(left.XMax.Equals(right.XMin),
                    $"z={z} i={i}: max {left.XMax:R} != next min {right.XMin:R}");
            }
        }
    }

    [Theory, MemberData(nameof(Roots))]
    public void Edge_IsBitIdentical_AcrossZoomLevels(Bbox root)
    {
        for (int z = 0; z <= 8; z++)
            for (long i = 0; i <= 1L << z; i++)
                for (int k = 1; z + k <= 12; k++)
                {
                    double shallow = TileMath.Edge(root.MinX, root.MaxX, 1L << z, i);
                    double deep = TileMath.Edge(root.MinX, root.MaxX, 1L << (z + k), i << k);
                    Assert.True(shallow.Equals(deep), $"Edge(z={z}, i={i}) != Edge(z={z + k}, i={i << k})");
                }
    }

    [Theory, MemberData(nameof(Roots))]
    public void EveryBoundaryPoint_LandsInExactlyOneLeaf_OfAMixedDepthCut(Bbox root)
    {
        var leaves = MixedDepthCut(maxZ: 4);

        foreach (double px in Probes(root, leaves))
            foreach (double py in Probes(root, leaves))
            {
                int hits = leaves.Count(t => TileMath.Contains(root, t, px, py));
                Assert.True(hits == 1, $"({px:R}, {py:R}) is in {hits} leaves, expected exactly 1");
            }
    }

    [Theory, MemberData(nameof(Roots))]
    public void PointToTile_AgreesWithTileRect_OnBoundaries(Bbox root)
    {
        for (int z = 0; z <= 12; z++)
        {
            long n = 1L << z;
            foreach (long i in EdgeIndices(n))
            {
                double edge = TileMath.Edge(root.MinX, root.MaxX, n, i);
                foreach (double p in Neighbourhood(edge, root))
                {
                    var (x, y) = TileMath.PointToTile(root, z, p, p);
                    Assert.True(TileMath.Contains(root, new TileId(z, x, y), p, p),
                        $"z={z}: PointToTile({p:R}) → {x}, whose rect excludes it");
                }
            }
        }
    }

    /// <summary>A quadtree cut with leaves at several depths, so the probes cross seams between tiles of
    /// different sizes — where a max edge at one zoom meets a min edge at another.</summary>
    private static IReadOnlyList<TileId> MixedDepthCut(int maxZ)
    {
        var leaves = new List<TileId>();
        void Recurse(TileId t)
        {
            if (t.Z >= maxZ || (t.Z > 0 && (t.Z + t.X + t.Y) % 3 == 0))
            {
                leaves.Add(t);
                return;
            }
            for (int q = 0; q < 4; q++) Recurse(t.Child(q));
        }
        Recurse(new TileId(0, 0, 0));
        return leaves;
    }

    /// <summary>Every distinct x edge of the cut, plus the doubles either side of it.</summary>
    private static IReadOnlyList<double> Probes(Bbox root, IReadOnlyList<TileId> leaves)
    {
        var edges = new SortedSet<double>();
        foreach (var t in leaves)
        {
            var (xMin, _, xMax, _) = TileMath.TileRect(root, t);
            edges.Add(xMin);
            edges.Add(xMax);
        }
        return edges.SelectMany(e => Neighbourhood(e, root)).Distinct().ToList();
    }

    private static IEnumerable<double> Neighbourhood(double edge, Bbox root)
    {
        foreach (double p in new[] { Math.BitDecrement(edge), edge, Math.BitIncrement(edge) })
            if (p >= root.MinX && p <= root.MaxX)
                yield return p;
    }

    private static IEnumerable<long> EdgeIndices(long n) =>
        n <= 64 ? Enumerable.Range(0, (int)n + 1).Select(i => (long)i)
                : new[] { 0L, 1L, 2L, n / 3, n / 2, n - 2, n - 1, n };
}
