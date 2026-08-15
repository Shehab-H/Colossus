namespace Colossus.Application.Abstractions.Sources;

public interface ISourceAdapterCatalog
{
    IReadOnlyList<SourceAdapterDescriptor> List();
    ISourceAdapter Resolve(string adapter);
}
