namespace Colossus.Application.Catalog;

public class CatalogException(string message, Exception? inner = null) : Exception(message, inner)
{
}

public sealed class CatalogNotFoundException(string message) : CatalogException(message)
{
}

public sealed class CatalogConflictException(string message, Exception? inner = null) : CatalogException(message, inner)
{
}

public sealed class CatalogConcurrencyException(string message) : CatalogException(message)
{
}

public sealed class CatalogRelationshipException(string message) : CatalogException(message)
{
}

public sealed class CatalogValidationException(
    IReadOnlyDictionary<string, string[]> errors,
    string message = "The catalog definition is invalid.") : CatalogException(message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
