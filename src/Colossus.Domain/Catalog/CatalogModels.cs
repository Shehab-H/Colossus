using Colossus.Domain.Model;

namespace Colossus.Domain.Catalog;

public static class CatalogScope
{
    public const string DefaultWorkspace = "default";

    public static void ValidateSlug(string slug, string field)
    {
        if (!ViewConfig.IsValidId(slug))
            throw new ArgumentException($"{field} '{slug}' must be non-empty kebab-case ([a-z0-9-])");
    }
}

public sealed record ConnectionDraft
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string Adapter { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public void Validate()
    {
        CatalogScope.ValidateSlug(Slug, "connection slug");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("connection name is required");
        if (string.IsNullOrWhiteSpace(Adapter)) throw new ArgumentException("connection adapter is required");
        string[] sensitive =
            ["password", "secret", "token", "api-key", "apikey", "credential", "connection-string", "connectionstring", "dsn"];
        if (Settings.Keys.Any(key => sensitive.Any(marker => key.Contains(marker, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("connection secrets must be supplied through the secret input, not settings");
        if (Settings.Values.Any(value =>
                Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)))
            throw new ArgumentException("connection URLs containing credentials must separate them into the secret input");
    }

    public override string ToString() => $"ConnectionDraft {{ Slug = {Slug}, Name = {Name}, Adapter = {Adapter} }}";
}

public sealed record CatalogConnection(
    Guid Id,
    string WorkspaceKey,
    ConnectionDraft Draft,
    long Revision,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public override string ToString() =>
        $"CatalogConnection {{ Id = {Id}, WorkspaceKey = {WorkspaceKey}, Slug = {Draft.Slug}, " +
        $"Adapter = {Draft.Adapter}, Revision = {Revision}, Archived = {Archived} }}";
}

public sealed record DatasetSource
{
    public required string Query { get; init; }
    public required GeometrySpec Geometry { get; init; }
    public IReadOnlyList<ChannelSpec> Channels { get; init; } = Array.Empty<ChannelSpec>();

    public SourceSpec ToSnapshot(
        string adapter,
        IReadOnlyDictionary<string, string>? settings = null) => new()
    {
        Adapter = adapter,
        Query = Query,
        Geometry = Geometry,
        Channels = Channels,
        Settings = settings ?? new Dictionary<string, string>(),
    };

    public static DatasetSource FromSnapshot(SourceSpec source) => new()
    {
        Query = source.Query,
        Geometry = source.Geometry,
        Channels = source.Channels,
    };
}

public sealed record DatasetDraft
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required Guid ConnectionId { get; init; }
    public required DatasetSource Source { get; init; }

    public void Validate()
    {
        CatalogScope.ValidateSlug(Slug, "dataset slug");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("dataset name is required");
        if (ConnectionId == Guid.Empty) throw new ArgumentException("dataset connection is required");
        if (Source is null || string.IsNullOrWhiteSpace(Source.Query))
            throw new ArgumentException("dataset source query is required");
        Source.Geometry.Validate(Slug);
    }
}

public sealed record CatalogDataset(
    Guid Id,
    string WorkspaceKey,
    DatasetDraft Draft,
    long Revision,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MapDraft
{
    public int SchemaVersion { get; init; } = 1;
    public required string Slug { get; init; }
    public string? Title { get; init; }
    public required Guid DatasetId { get; init; }
    public required Viewport Viewport { get; init; }
    public required Mark Mark { get; init; }
    public ReductionKind? Reduction { get; init; }
    public IReadOnlyList<string>? BakeFilters { get; init; }
    public IReadOnlyList<FilterSpec>? Filters { get; init; }
    public IReadOnlyList<MeasureSpec>? Measures { get; init; }
    public AggregateSpec? Aggregate { get; init; }
    public StorageSpec? Storage { get; init; }
    public EncodingSpec? Encoding { get; init; }
    public InspectSpec? Inspect { get; init; }

    public ViewConfig ToSnapshot(CatalogDataset dataset, CatalogConnection connection)
    {
        CatalogScope.ValidateSlug(Slug, "map slug");
        if (DatasetId == Guid.Empty || DatasetId != dataset.Id)
            throw new ArgumentException("map dataset relationship is invalid");
        if (dataset.Draft.ConnectionId != connection.Id)
            throw new ArgumentException("dataset connection relationship is invalid");

        var snapshot = new ViewConfig
        {
            SchemaVersion = SchemaVersion,
            Id = Slug,
            Title = Title,
            Viewport = Viewport,
            Mark = Mark,
            Reduction = Reduction,
            Source = dataset.Draft.Source.ToSnapshot(connection.Draft.Adapter, connection.Draft.Settings),
            BakeFilters = BakeFilters,
            Filters = Filters,
            Measures = Measures,
            Aggregate = Aggregate,
            Storage = Storage,
            Encoding = Encoding,
            Inspect = Inspect,
        };
        snapshot.Validate();
        return snapshot;
    }

    public static MapDraft FromSnapshot(ViewConfig view, Guid datasetId) => new()
    {
        SchemaVersion = view.SchemaVersion,
        Slug = view.Id,
        Title = view.Title,
        DatasetId = datasetId,
        Viewport = view.Viewport,
        Mark = view.Mark,
        Reduction = view.Reduction,
        BakeFilters = view.BakeFilters,
        Filters = view.Filters,
        Measures = view.Measures,
        Aggregate = view.Aggregate,
        Storage = view.Storage,
        Encoding = view.Encoding,
        Inspect = view.Inspect,
    };
}

public sealed record CatalogMap(
    Guid Id,
    string WorkspaceKey,
    MapDraft Draft,
    long Revision,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BakePolicy(
    Guid MapId,
    bool Enabled,
    TimeSpan? Interval,
    string TimeZoneId,
    bool AllowOverlap,
    int RetainSuccessfulReleases,
    long Revision,
    DateTimeOffset UpdatedAt)
{
    public void Validate()
    {
        if (MapId == Guid.Empty) throw new ArgumentException("bake policy map is required");
        if (Enabled && (Interval is null || Interval <= TimeSpan.Zero))
            throw new ArgumentException("an enabled bake policy requires a positive interval");
        if (Enabled && Interval < TimeSpan.FromSeconds(1))
            throw new ArgumentException("bake policy intervals must be at least one second");
        if (string.IsNullOrWhiteSpace(TimeZoneId)) throw new ArgumentException("bake policy time zone is required");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (TimeZoneNotFoundException e) { throw new ArgumentException($"unknown bake policy time zone '{TimeZoneId}'", e); }
        catch (InvalidTimeZoneException e) { throw new ArgumentException($"invalid bake policy time zone '{TimeZoneId}'", e); }
        if (RetainSuccessfulReleases < 1)
            throw new ArgumentException("bake policy must retain at least one successful release");
    }
}

public enum BakeRunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum BakeRunStage
{
    Queued,
    Probing,
    Extracting,
    Tiling,
    Verifying,
    Publishing,
    Cleanup,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record BakeRun(
    Guid Id,
    Guid MapId,
    long DraftRevision,
    ViewConfig Definition,
    BakeRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? Error = null,
    BakeRunStage Stage = BakeRunStage.Queued,
    double Progress = 0,
    string? StatusMessage = null,
    long? SourceRows = null,
    int? TileCount = null,
    long? ArtifactBytes = null,
    Guid? ReleaseId = null,
    string? FailureCategory = null,
    string? FailureDetails = null,
    bool CancellationRequested = false,
    int Attempt = 0,
    string? BackgroundJobId = null,
    DateTimeOffset? UpdatedAt = null)
{
    public bool IsTerminal => Status is BakeRunStatus.Succeeded or BakeRunStatus.Failed or BakeRunStatus.Cancelled;

    public BakeRun ValidateState()
    {
        if (Id == Guid.Empty || MapId == Guid.Empty) throw new ArgumentException("bake run and map IDs are required");
        if (Progress is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(Progress), "progress must be between 0 and 1");
        if (Attempt < 0) throw new ArgumentOutOfRangeException(nameof(Attempt));
        return this;
    }
}

public sealed record CatalogRelease(
    Guid Id,
    Guid MapId,
    Guid BakeRunId,
    string Version,
    string ArtifactLocation,
    DateTimeOffset PublishedAt,
    long? ArtifactBytes = null);
