namespace Colossus.Core.Model;

/// <summary>A node in the quadtree pyramid: zoom level Z and grid coords X,Y within that level.</summary>
public readonly record struct TileId(int Z, int X, int Y)
{
    /// <summary>Relative path of the tile's Arrow file, e.g. "3/5/2.arrow".</summary>
    public string RelativePath => $"{Z}/{X}/{Y}.arrow";

    public TileId Child(int quadrant) => new(Z + 1, X * 2 + (quadrant & 1), Y * 2 + ((quadrant >> 1) & 1));
}
