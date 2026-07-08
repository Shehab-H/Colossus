namespace Colossus.Domain.Model;

public readonly record struct TileId(int Z, int X, int Y)
{
    public string RelativePath => $"{Z}/{X}/{Y}.arrow";

    public TileId Child(int quadrant) => new(Z + 1, X * 2 + (quadrant & 1), Y * 2 + ((quadrant >> 1) & 1));
}
