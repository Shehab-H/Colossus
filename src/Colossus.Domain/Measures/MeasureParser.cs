namespace Colossus.Domain.Measures;

/// <summary>Recursive-descent parser for the closed measure grammar (VIEW_CONFIG §4). Syntax only —
/// it validates the shape of an expression and names the offending token, never whether a channel
/// exists or has the right role (that is <see cref="Colossus.Domain.Model.ViewConfig.Validate"/>).
/// The client mirror (web/src/lib/measures.ts) reproduces this exactly against the shared fixture
/// <c>tests/fixtures/measure-cases.json</c>.</summary>
public static class MeasureParser
{
    private static readonly HashSet<string> NumericVerbs =
        new(StringComparer.Ordinal) { "sum", "count", "avg", "wavg", "min", "max" };

    public static MeasureExpr Parse(string expr)
    {
        var tokens = Tokenize(expr);
        int pos = 0;
        var result = ParseExpr(tokens, ref pos, expr);
        var tok = tokens[pos];
        if (tok.Kind != TokKind.End)
            throw Err($"unexpected trailing token '{tok.Text}'", expr);
        return result;
    }

    private static MeasureExpr ParseExpr(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        string verb = ExpectIdent(t, ref pos, expr, "an aggregate verb");
        if (verb is "argmax" or "argmin")
            return ParseArgExt(verb, t, ref pos, expr);
        if (verb == "share")
            return ParseShare(t, ref pos, expr);
        var agg = ParseAggBody(verb, t, ref pos, expr);
        var where = ParseOptionalWhere(t, ref pos, expr);
        return where is null ? agg : agg with { Where = where };
    }

    private static ArgExt ParseArgExt(string verb, IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        Expect(t, ref pos, TokKind.LParen, expr);
        string dim = ExpectIdent(t, ref pos, expr, "a dimension channel");
        Expect(t, ref pos, TokKind.Comma, expr);
        var inner = ParseInnerAgg(t, ref pos, expr);
        Expect(t, ref pos, TokKind.RParen, expr);
        return new ArgExt(dim, inner, IsMax: verb == "argmax");
    }

    private static Share ParseShare(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        Expect(t, ref pos, TokKind.LParen, expr);
        var inner = ParseInnerAgg(t, ref pos, expr);
        Expect(t, ref pos, TokKind.RParen, expr);
        var where = ParseOptionalWhere(t, ref pos, expr)
            ?? throw Err("share(...) requires a 'where' clause", expr);
        return new Share(inner, where.Channel, where.Value);
    }

    /// <summary>An inner agg (argmax/share operand): one of the six numeric verbs, and — per §4 — it
    /// may not itself carry a where.</summary>
    private static Agg ParseInnerAgg(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        string verb = ExpectIdent(t, ref pos, expr, "an aggregate verb");
        if (!NumericVerbs.Contains(verb))
            throw Err($"expected an aggregate verb (sum, count, avg, wavg, min, max), got '{verb}'", expr);
        var agg = ParseAggBody(verb, t, ref pos, expr);
        if (t[pos].Kind == TokKind.Ident && t[pos].Text == "where")
            throw Err("an inner aggregate may not carry a 'where' clause", expr);
        return agg;
    }

    private static Agg ParseAggBody(string verb, IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        switch (verb)
        {
            case "sum": return new Sum(Unary(t, ref pos, expr));
            case "avg": return new Avg(Unary(t, ref pos, expr));
            case "min": return new Min(Unary(t, ref pos, expr));
            case "max": return new Max(Unary(t, ref pos, expr));
            case "count":
                Expect(t, ref pos, TokKind.LParen, expr);
                Expect(t, ref pos, TokKind.RParen, expr);
                return new Count();
            case "wavg":
                Expect(t, ref pos, TokKind.LParen, expr);
                string ch = ExpectIdent(t, ref pos, expr, "a numeric channel");
                Expect(t, ref pos, TokKind.Comma, expr);
                string w = ExpectIdent(t, ref pos, expr, "a weight channel");
                Expect(t, ref pos, TokKind.RParen, expr);
                return new Wavg(ch, w);
            default:
                throw Err($"unknown aggregate verb '{verb}'", expr);
        }
    }

    private static string Unary(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        Expect(t, ref pos, TokKind.LParen, expr);
        string ch = ExpectIdent(t, ref pos, expr, "a numeric channel");
        Expect(t, ref pos, TokKind.RParen, expr);
        return ch;
    }

    private static WhereClause? ParseOptionalWhere(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        if (t[pos].Kind != TokKind.Ident || t[pos].Text != "where") return null;
        pos++;
        string ch = ExpectIdent(t, ref pos, expr, "a dimension channel");
        Expect(t, ref pos, TokKind.Eq, expr);
        string val = ExpectString(t, ref pos, expr);
        return new WhereClause(ch, val);
    }

    private static string ExpectIdent(IReadOnlyList<Tok> t, ref int pos, string expr, string what)
    {
        var tok = t[pos];
        if (tok.Kind != TokKind.Ident)
            throw Err($"expected {what}, got '{tok.Text}'", expr);
        pos++;
        return tok.Text;
    }

    private static string ExpectString(IReadOnlyList<Tok> t, ref int pos, string expr)
    {
        var tok = t[pos];
        if (tok.Kind != TokKind.Str)
            throw Err($"expected a quoted literal, got '{tok.Text}'", expr);
        pos++;
        return tok.Text;
    }

    private static void Expect(IReadOnlyList<Tok> t, ref int pos, TokKind kind, string expr)
    {
        var tok = t[pos];
        if (tok.Kind != kind)
            throw Err($"expected '{Punct(kind)}', got '{tok.Text}'", expr);
        pos++;
    }

    private static string Punct(TokKind k) => k switch
    {
        TokKind.LParen => "(",
        TokKind.RParen => ")",
        TokKind.Comma => ",",
        TokKind.Eq => "=",
        _ => k.ToString(),
    };

    private static MeasureParseException Err(string message, string expr) =>
        new($"{message} in \"{expr}\"");

    private enum TokKind { Ident, Str, LParen, RParen, Comma, Eq, End }

    private readonly record struct Tok(TokKind Kind, string Text);

    private static List<Tok> Tokenize(string expr)
    {
        var tokens = new List<Tok>();
        int i = 0;
        while (i < expr.Length)
        {
            char c = expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '(': tokens.Add(new(TokKind.LParen, "(")); i++; continue;
                case ')': tokens.Add(new(TokKind.RParen, ")")); i++; continue;
                case ',': tokens.Add(new(TokKind.Comma, ",")); i++; continue;
                case '=': tokens.Add(new(TokKind.Eq, "=")); i++; continue;
                case '\'':
                {
                    int start = ++i;
                    while (i < expr.Length && expr[i] != '\'') i++;
                    if (i >= expr.Length)
                        throw Err("unterminated string literal", expr);
                    tokens.Add(new(TokKind.Str, expr[start..i]));
                    i++; // closing quote
                    continue;
                }
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < expr.Length && (char.IsAsciiLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                tokens.Add(new(TokKind.Ident, expr[start..i]));
                continue;
            }
            throw Err($"unexpected character '{c}'", expr);
        }
        tokens.Add(new(TokKind.End, "<end>"));
        return tokens;
    }
}
