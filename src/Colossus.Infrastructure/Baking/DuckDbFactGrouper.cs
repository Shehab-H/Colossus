using System.Text;
using Colossus.Domain.Baking;
using Colossus.Domain.Measures;
using Colossus.Domain.Model;
using Colossus.Domain.Tiling;
using Colossus.Infrastructure.DuckDb;
using Colossus.Infrastructure.Tiling;

namespace Colossus.Infrastructure.Baking;

/// <summary>Groups a group-regime view's facts into a marks table with one GROUP BY over the geometry
/// key (the representative point). Emits <c>id</c>, geometry, perMark channels via <c>first()</c>, and
/// every measure at the default context — flat aggregates in the main grouping, argmax/argmin via a
/// per-dimension sub-grouping joined back. The measure SQL is rendered straight from the parsed AST;
/// the client fold mirrors the same finalization over baked partials.</summary>
public sealed class DuckDbFactGrouper : IFactGrouper
{
    public FactGrouping GroupToMarks(string factsParquetPath, string marksParquetPath, ViewConfig view)
    {
        string workDir = Path.GetDirectoryName(Path.GetFullPath(marksParquetPath))!;
        using var db = DuckDbSession.OnDisk(workDir);
        string facts = $"read_parquet('{Sql.Path(factsParquetPath)}')";
        var present = Columns(db, facts);

        var grouping = Classify(db, facts, view, present);
        string sql = BuildMarksSql(facts, view, grouping, present);
        db.Exec($"COPY ({sql}) TO '{Sql.Path(marksParquetPath)}' (FORMAT PARQUET)");
        return grouping;
    }

    private static HashSet<string> Columns(DuckDbSession db, string facts)
    {
        var cols = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT column_name FROM (DESCRIBE SELECT * FROM {facts})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(0));
        return cols;
    }

    // A channel is perFact iff the number of distinct (mark, value) tuples exceeds the number of marks —
    // i.e. some mark carries more than one value. One pass over the facts for the whole classification.
    private static FactGrouping Classify(DuckDbSession db, string facts, ViewConfig view, ISet<string> present)
    {
        var channels = view.Source.Channels.Where(c => present.Contains(c.Name)).Select(c => c.Name).ToList();
        var sb = new StringBuilder($"SELECT count(DISTINCT ({TileSchema.X}, {TileSchema.Y})) AS marks");
        foreach (var c in channels)
            sb.Append($", count(DISTINCT ({TileSchema.X}, {TileSchema.Y}, {Quote(c)}))");
        sb.Append($" FROM {facts}");

        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = sb.ToString();
        using var r = cmd.ExecuteReader();
        r.Read();
        long marks = Convert.ToInt64(r.GetValue(0));
        var perMark = new List<string>();
        var perFact = new List<string>();
        for (int i = 0; i < channels.Count; i++)
            (Convert.ToInt64(r.GetValue(i + 1)) > marks ? perFact : perMark).Add(channels[i]);
        return new FactGrouping(perMark, perFact);
    }

    private static string BuildMarksSql(string facts, ViewConfig view, FactGrouping g, ISet<string> present)
    {
        var parsed = view.Measures!.Select(m => (m.Name, Ast: MeasureParser.Parse(m.Expr))).ToList();

        var ctes = new List<string> { $"f AS (SELECT * FROM {facts})" };
        var argJoins = new List<string>();
        var argCols = new List<string>();
        int i = 0;
        foreach (var (name, ast) in parsed)
        {
            if (ast is not ArgExt arg) continue;
            string dim = $"dim{i}", am = $"am{i}", fn = arg.IsMax ? "arg_max" : "arg_min";
            ctes.Add($"{dim} AS (SELECT {TileSchema.X}, {TileSchema.Y}, {Quote(arg.Dimension)} AS dv, " +
                     $"{AggExpr(arg.Inner, null)} AS iv FROM f GROUP BY {TileSchema.X}, {TileSchema.Y}, {Quote(arg.Dimension)})");
            ctes.Add($"{am} AS (SELECT {TileSchema.X}, {TileSchema.Y}, {fn}(dv, iv) AS {Quote(name)} " +
                     $"FROM {dim} GROUP BY {TileSchema.X}, {TileSchema.Y})");
            argJoins.Add($"JOIN {am} USING ({TileSchema.X}, {TileSchema.Y})");
            argCols.Add($"{am}.{Quote(name)}");
            i++;
        }

        var marks = new StringBuilder($"SELECT {TileSchema.X}, {TileSchema.Y}");
        if (present.Contains(TileSchema.Geometry)) marks.Append($", first({TileSchema.Geometry}) AS {TileSchema.Geometry}");
        if (present.Contains(TileSchema.PartOffsets)) marks.Append($", first({TileSchema.PartOffsets}) AS {TileSchema.PartOffsets}");
        foreach (var ch in g.PerMarkChannels) marks.Append($", first({Quote(ch)}) AS {Quote(ch)}");
        foreach (var (name, ast) in parsed)
            if (ast is not ArgExt) marks.Append($", ({FlatMeasure(ast)})::FLOAT AS {Quote(name)}");
        marks.Append($" FROM f GROUP BY {TileSchema.X}, {TileSchema.Y}");
        ctes.Add($"marks AS ({marks})");

        var outSel = new StringBuilder($"SELECT {MarkKey.RealSql($"m.{TileSchema.X}", $"m.{TileSchema.Y}")} AS {TileSchema.Id}, m.*");
        foreach (var c in argCols) outSel.Append($", {c}");
        outSel.Append($" FROM marks m {string.Join(" ", argJoins)}");

        return $"WITH {string.Join(",\n", ctes)}\n{outSel}";
    }

    private static string FlatMeasure(MeasureExpr e) => e switch
    {
        Agg a => AggExpr(a, a.Where),
        // Part/whole: an absent restricted set contributes 0 (COALESCE), not NULL; a zero whole is
        // undefined (nullif → NULL → NaN, the unknown-color case). The client fold mirrors this.
        Share sh => $"(COALESCE({AggExpr(sh.Inner, new WhereClause(sh.WhereChannel, sh.WhereValue))}, 0)) " +
                    $"/ nullif({AggExpr(sh.Inner, null)}, 0)",
        _ => throw new InvalidOperationException($"non-flat measure {e.GetType().Name}"),
    };

    // One aggregate at the default context, optionally restricted by a where (rendered as a FILTER).
    private static string AggExpr(Agg a, WhereClause? filter)
    {
        string f = filter is null ? "" : $" FILTER (WHERE {Quote(filter.Channel)} = {StrLit(filter.Value)})";
        return a switch
        {
            Sum s => $"sum({Quote(s.Channel)}){f}",
            Count => $"count(*){f}",
            Avg av => $"avg({Quote(av.Channel)}){f}",
            Min m => $"min({Quote(m.Channel)}){f}",
            Max m => $"max({Quote(m.Channel)}){f}",
            Wavg w => $"sum({Quote(w.Channel)} * {Quote(w.Weight)}){f} / nullif(sum({Quote(w.Weight)}){f}, 0)",
            _ => throw new InvalidOperationException($"unhandled agg {a.GetType().Name}"),
        };
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
    private static string StrLit(string value) => $"'{value.Replace("'", "''")}'";
}
