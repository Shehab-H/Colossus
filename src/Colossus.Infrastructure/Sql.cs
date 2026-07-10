using System.Globalization;

namespace Colossus.Infrastructure;

/// <summary>Fragment helpers for the SQL the bake composes (ClickHouse and DuckDB).</summary>
public static class Sql
{
    /// <summary>Round-trip numeric literal, immune to the host culture's decimal separator.</summary>
    public static string Lit(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Round-trip literal explicitly typed DOUBLE. DuckDB reads a bare decimal literal as
    /// DECIMAL and evaluates it in exact decimal arithmetic, so <c>a + i * b</c> over literals does not
    /// round the way IEEE754 does. Any expression that has to agree with the C# original bit for bit must
    /// spell the type out.</summary>
    public static string Dbl(double value) => Lit(value) + "::DOUBLE";

    /// <summary>Absolute path with forward slashes — the one spelling both engines accept quoted.</summary>
    public static string Path(string path) => System.IO.Path.GetFullPath(path).Replace('\\', '/');
}
