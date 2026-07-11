using Colossus.Domain.Model;

namespace Colossus.Domain.Baking;

/// <summary>The derived perMark/perFact split of a group-regime view's channels (VIEW_CONFIG §1). A
/// channel is perFact iff its value varies within some mark; the rest are perMark (mark attributes).
/// perMark channels are carried on the marks table; perFact channels live only in fact companions.</summary>
public sealed record FactGrouping(
    IReadOnlyList<string> PerMarkChannels,
    IReadOnlyList<string> PerFactChannels);

/// <summary>Groups a group-regime view's fact staging into a marks table — one row per mark (distinct
/// geometry), carrying its perMark channels and every measure at the default context (all facts). The
/// marks table is what the reducers and domain scanner then see; the raw facts stay for the companions
/// (GROUP-MEASURES §2). A no-op contract in the row regime — only called when the view has measures.</summary>
public interface IFactGrouper
{
    FactGrouping GroupToMarks(string factsParquetPath, string marksParquetPath, ViewConfig view);
}
