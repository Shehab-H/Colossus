namespace Colossus.Application.Abstractions.Sources;

public interface ISourceAdapterCatalog
{
    ISourceAdapter Resolve(string adapter);
}
