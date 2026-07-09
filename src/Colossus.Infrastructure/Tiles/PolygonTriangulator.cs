namespace Colossus.Infrastructure.Tiles;

/// <summary>Bake-time polygon tessellation. Polygon tiles carry per-row triangle indices (the
/// <c>triangles</c> column) so the client never triangulates: deck.gl accepts external indices and skips
/// its per-polygon main-thread earcut — the synchronous block that made render stutter scale with cell
/// count. Ear clipping handles any simple ring (convex or concave, either winding, closed or open);
/// multi-part rows tessellate each part independently. Indices are local to the row's vertex list
/// (coordinate pairs), so the client just offsets them by each row's vertex start — one add per index.</summary>
public static class PolygonTriangulator
{
    /// <summary>Triangulates one row's flat interleaved [x0,y0,x1,y1,…] ring(s) into vertex indices.
    /// <paramref name="partOffsets"/> are vertex positions delimiting parts ([0,5] = one 5-vertex ring);
    /// null treats the whole list as a single ring.</summary>
    public static List<int> Triangulate(float[] coords, int[]? partOffsets)
    {
        var result = new List<int>();
        int vertexCount = coords.Length / 2;
        if (partOffsets is { Length: >= 2 })
            for (int p = 0; p + 1 < partOffsets.Length; p++)
                ClipRing(coords, partOffsets[p], Math.Min(partOffsets[p + 1], vertexCount), result);
        else
            ClipRing(coords, 0, vertexCount, result);
        return result;
    }

    private static void ClipRing(float[] c, int start, int end, List<int> result)
    {
        int m = end - start;
        // A closed ring repeats its first vertex last — triangulate the unique vertices only.
        if (m >= 2 && c[2 * start] == c[2 * (end - 1)] && c[2 * start + 1] == c[2 * (end - 1) + 1]) m--;
        if (m < 3) return;

        var ring = new List<int>(m);
        for (int i = 0; i < m; i++) ring.Add(start + i);

        // Winding from the signed area — ear convexity is judged relative to it, so both windings work.
        double area = 0;
        for (int i = 0; i < m; i++)
        {
            int j = (i + 1) % m;
            area += (double)c[2 * ring[i]] * c[2 * ring[j] + 1] - (double)c[2 * ring[j]] * c[2 * ring[i] + 1];
        }
        double sign = area >= 0 ? 1 : -1;

        while (ring.Count > 3)
        {
            bool clipped = false;
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[(i + ring.Count - 1) % ring.Count];
                int b = ring[i];
                int d = ring[(i + 1) % ring.Count];
                if (!IsEar(c, a, b, d, ring, sign)) continue;
                result.Add(a);
                result.Add(b);
                result.Add(d);
                ring.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                // Numerically degenerate remainder (self-touching / collinear soup): fan it out rather
                // than dropping the fill — a sliver of overdraw beats a hole in the choropleth.
                for (int i = 1; i + 1 < ring.Count; i++)
                {
                    result.Add(ring[0]);
                    result.Add(ring[i]);
                    result.Add(ring[i + 1]);
                }
                return;
            }
        }
        result.Add(ring[0]);
        result.Add(ring[1]);
        result.Add(ring[2]);
    }

    private static bool IsEar(float[] c, int a, int b, int d, List<int> ring, double sign)
    {
        double ax = c[2 * a], ay = c[2 * a + 1];
        double bx = c[2 * b], by = c[2 * b + 1];
        double dx = c[2 * d], dy = c[2 * d + 1];
        // Reflex corner → not an ear. Collinear (cross == 0) counts as clippable: it emits a zero-area
        // triangle the GPU won't draw, and guarantees the loop always shrinks.
        double cross = (bx - ax) * (dy - ay) - (dx - ax) * (by - ay);
        if (cross * sign < 0) return false;
        foreach (int v in ring)
        {
            if (v == a || v == b || v == d) continue;
            if (PointInTriangle(c[2 * v], c[2 * v + 1], ax, ay, bx, by, dx, dy)) return false;
        }
        return true;
    }

    private static bool PointInTriangle(double px, double py,
        double ax, double ay, double bx, double by, double cx, double cy)
    {
        double s1 = (bx - ax) * (py - ay) - (px - ax) * (by - ay);
        double s2 = (cx - bx) * (py - by) - (px - bx) * (cy - by);
        double s3 = (ax - cx) * (py - cy) - (px - cx) * (ay - cy);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
    }
}
