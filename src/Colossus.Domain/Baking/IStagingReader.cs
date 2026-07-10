namespace Colossus.Domain.Baking;

/// <summary>Reads the staged extract a bake consumed. The fidelity check needs a row count the reducer
/// never produced; this is the closest independent witness to what it actually read.</summary>
public interface IStagingReader
{
    bool Exists(string stagingPath);
    long RowCount(string stagingPath);
}
