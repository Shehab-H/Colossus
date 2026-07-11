namespace Colossus.Domain.Measures;

/// <summary>A parsed measure expression (VIEW_CONFIG §4 grammar) — the typed AST the bake renders to
/// DuckDB SQL and the client mirrors to a fold plan. Pure and source-independent; carries no channel
/// metadata (semantic checks live in <see cref="Colossus.Domain.Model.ViewConfig"/>).</summary>
public abstract record MeasureExpr;

/// <summary><c>where ch = 'v'</c> — restrict facts to a perFact dict channel equalling a literal.</summary>
public sealed record WhereClause(string Channel, string Value);

/// <summary>A numeric aggregate over a mark's surviving facts, optionally restricted by a where.</summary>
public abstract record Agg : MeasureExpr
{
    public WhereClause? Where { get; init; }
}

public sealed record Sum(string Channel) : Agg;
public sealed record Count : Agg;
public sealed record Avg(string Channel) : Agg;
public sealed record Wavg(string Channel, string Weight) : Agg;
public sealed record Min(string Channel) : Agg;
public sealed record Max(string Channel) : Agg;

/// <summary><c>share(inner) where ch = 'v'</c> — inner restricted by the where, divided by inner
/// unrestricted; a fraction in 0..1. The where is required (it is what makes it a share).</summary>
public sealed record Share(Agg Inner, string WhereChannel, string WhereValue) : MeasureExpr;

/// <summary><c>argmax|argmin(dim, inner)</c> — the dim value whose group extremizes the inner agg.
/// Categorical output over the dim's domain.</summary>
public sealed record ArgExt(string Dimension, Agg Inner, bool IsMax) : MeasureExpr;

public sealed class MeasureParseException(string message) : Exception(message);
