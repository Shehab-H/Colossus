namespace Colossus.Domain.Sources;

public interface ISourceAdapterCatalog
{
    ISourceAdapter Resolve(string adapter);
}
