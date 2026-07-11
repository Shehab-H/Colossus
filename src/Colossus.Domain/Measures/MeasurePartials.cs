namespace Colossus.Domain.Measures;

public enum PartialKind { Sum, Count, Swp, Min, Max }

/// <summary>One partial-aggregate column a companion carries, with the deterministic name both the bake
/// writer and the client fold address it by (the fourth cross-language contract). Additive across facts,
/// so grid-cell merging and context folding are the same operation at any LOD.</summary>
public sealed record Partial(PartialKind Kind, string? Channel = null, string? Weight = null)
{
    public string Name => Kind switch
    {
        PartialKind.Count => "cnt",
        PartialKind.Sum => $"sum__{Channel}",
        PartialKind.Swp => $"swp__{Channel}__{Weight}",
        PartialKind.Min => $"min__{Channel}",
        PartialKind.Max => $"max__{Channel}",
        _ => throw new InvalidOperationException($"unhandled partial {Kind}"),
    };
}

/// <summary>The union of partials the declared measures need, first-seen order, de-duplicated by name
/// (VIEW_CONFIG §4): <c>sum→sum</c>, <c>count→cnt</c>, <c>avg→sum+cnt</c>, <c>wavg→swp+sum(w)</c>,
/// <c>min/max→min/max</c>; <c>share</c>/<c>argmax</c> reduce to their inner agg's partials (their dims
/// are already grain). The client mirror computes the identical set.</summary>
public static class MeasurePartials
{
    public static IReadOnlyList<Partial> For(IEnumerable<MeasureExpr> measures)
    {
        var order = new List<Partial>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(Partial p) { if (seen.Add(p.Name)) order.Add(p); }
        void Walk(MeasureExpr e)
        {
            switch (e)
            {
                case Sum s: Add(new(PartialKind.Sum, s.Channel)); break;
                case Count: Add(new(PartialKind.Count)); break;
                case Avg a: Add(new(PartialKind.Sum, a.Channel)); Add(new(PartialKind.Count)); break;
                case Wavg w: Add(new(PartialKind.Swp, w.Channel, w.Weight)); Add(new(PartialKind.Sum, w.Weight)); break;
                case Min m: Add(new(PartialKind.Min, m.Channel)); break;
                case Max m: Add(new(PartialKind.Max, m.Channel)); break;
                case Share sh: Walk(sh.Inner); break;
                case ArgExt ax: Walk(ax.Inner); break;
            }
        }
        foreach (var m in measures) Walk(m);
        return order;
    }
}
