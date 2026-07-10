using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

/// <summary>Scans the staged extract for each declared channel's data domain (distinct values or
/// numeric range/quantiles) so the manifest can answer what the client otherwise derives from a
/// root-tile sample. One scan per bake, over every row.</summary>
public interface IChannelDomainScanner
{
    IReadOnlyDictionary<string, ChannelDomain> Scan(string stagingParquetPath, ViewConfig view);
}
