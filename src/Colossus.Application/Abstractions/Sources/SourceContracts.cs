using Colossus.Domain.Catalog;
using Colossus.Domain.Model;

namespace Colossus.Application.Abstractions.Sources;

public sealed record SourceConfigurationField(
    string Key,
    string Label,
    bool Required,
    bool Secret = false,
    string? Placeholder = null,
    IReadOnlyList<string>? AllowedValues = null);

public sealed record SourceAdapterCapabilities(
    bool ConnectionValidation,
    bool LocationDiscovery,
    bool SchemaInspection,
    bool BoundedPreview,
    bool CanonicalExtraction);

public sealed record SourceAdapterDescriptor(
    string Id,
    string Name,
    SourceAdapterCapabilities Capabilities,
    IReadOnlyList<SourceConfigurationField> ConfigurationFields,
    IReadOnlyList<GeometryKind> GeometryKinds,
    string DatasetInputLabel = "Source query",
    string DatasetInputDescription = "Schema and preview use this query without launching a bake.",
    string DatasetInputPlaceholder = "SELECT lon, lat, value FROM analytics.coverage");

public sealed record SourceConnectionContext(
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> Secrets);

public sealed record SourceConnectionValidation(
    string Message,
    IReadOnlyDictionary<string, string> Details);

public sealed record SourceLocation(string Namespace, string Name, string Kind);

public sealed record SourceSchemaRequest(
    SourceConnectionContext Connection,
    string Query);

public sealed record SourceSchemaColumn(
    string Name,
    string SourceType,
    bool Nullable);

public sealed record SourceSchema(
    IReadOnlyList<SourceSchemaColumn> Columns,
    IReadOnlyList<string>? Warnings = null);

public sealed record SourcePreviewBudget(
    int MaxRows,
    long MaxResultBytes,
    long MaxScannedRows,
    TimeSpan MaxDuration)
{
    public static SourcePreviewBudget Default { get; } = new(
        MaxRows: 50,
        MaxResultBytes: 1 * 1024 * 1024,
        MaxScannedRows: 50_000_000,
        MaxDuration: TimeSpan.FromSeconds(10));

    public SourcePreviewBudget ConstrainTo(SourcePreviewBudget ceiling)
    {
        if (MaxRows <= 0 || MaxResultBytes <= 0 || MaxScannedRows <= 0 || MaxDuration <= TimeSpan.Zero)
            throw new ArgumentException("Preview budgets must be positive.", nameof(SourcePreviewBudget));
        return new SourcePreviewBudget(
            Math.Min(MaxRows, ceiling.MaxRows),
            Math.Min(MaxResultBytes, ceiling.MaxResultBytes),
            Math.Min(MaxScannedRows, ceiling.MaxScannedRows),
            MaxDuration <= ceiling.MaxDuration ? MaxDuration : ceiling.MaxDuration);
    }
}

public sealed record SourcePreviewColumn(string Name, string SourceType, bool Nullable, string Role);

public sealed record SourceGeometryPreview(
    GeometryKind Kind,
    bool Geographic,
    Bbox Bounds,
    int SampledRows,
    int DistinctGeometries);

public sealed record SourcePreview(
    IReadOnlyList<SourcePreviewColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
    SourceGeometryPreview Geometry,
    bool Truncated,
    long ResultBytes,
    TimeSpan Elapsed,
    IReadOnlyList<string>? Warnings = null);

public sealed record SourceExtractProgress(long BytesWritten, bool Completed = false, long? RowsRead = null);

public enum SourceErrorKind
{
    Configuration,
    Connectivity,
    Query,
    Type,
    Cancellation,
    TransientFailure,
}

public sealed class SourceOperationException(
    SourceErrorKind kind,
    string message,
    IReadOnlyDictionary<string, string[]>? fieldErrors = null,
    bool transient = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public SourceErrorKind Kind { get; } = kind;
    public IReadOnlyDictionary<string, string[]> FieldErrors { get; } =
        fieldErrors ?? new Dictionary<string, string[]>();
    public bool Transient { get; } = transient;
}

public sealed record SourcePreviewRequest(
    SourceConnectionContext Connection,
    DatasetSource Dataset,
    SourcePreviewBudget Budget);
