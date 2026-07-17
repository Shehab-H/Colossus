using System.Text.Json.Serialization;

namespace Colossus.Infrastructure.Fold;

/// <summary>The R4 fold request body the client POSTs (mirrors web/src/lib/measures.ts FoldContext + the
/// remote executor payload). <see cref="Context"/> is the already-compiled fold context (perFact equality
/// selections + temporal ranges), so the server never re-derives the perMark/perFact classification.</summary>
public sealed class FoldQuery
{
    /// <summary>Pin the fold to a specific baked version; null resolves to the view's latest.</summary>
    public string? Version { get; set; }

    /// <summary>Measure names to fold (a subset of the view's measures — the active color measure for the
    /// map, or the inspect set for a tooltip).</summary>
    public List<string> Measures { get; set; } = [];

    public FoldContextDto Context { get; set; } = new();

    /// <summary>Tile keys "z/x/y" to fold — the on-screen viewport.</summary>
    public List<string> Tiles { get; set; } = [];
}

/// <summary>The active fold context (VIEW_CONFIG §1): perFact equality selections + temporal ranges.</summary>
public sealed class FoldContextDto
{
    // 'equals' can't be a C# property name (it hides object.Equals); bind the JSON key explicitly.
    [JsonPropertyName("equals")]
    public Dictionary<string, string>? Equals_ { get; set; }

    public Dictionary<string, FoldRangeDto>? Ranges { get; set; }
}

/// <summary>An inclusive, possibly open temporal range (matches web/src/lib/dates.ts DateRange).</summary>
public sealed class FoldRangeDto
{
    public string? From { get; set; }
    public string? To { get; set; }
}
