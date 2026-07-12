namespace Colossus.Domain.Baking;

public interface ITileReader
{
    long RowCount(string tilePath);
    bool Exists(string tilePath);
}
