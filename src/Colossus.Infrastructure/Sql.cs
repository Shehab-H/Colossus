using System.Globalization;

namespace Colossus.Infrastructure;

/// <summary>Fragment helpers for the SQL the bake composes (ClickHouse and DuckDB).</summary>
public static class Sql
{
    /// <summary>Round-trip numeric literal, immune to the host culture's decimal separator.</summary>
    public static string Lit(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Absolute path with forward slashes — the one spelling both engines accept quoted.</summary>
    public static string Path(string path) => System.IO.Path.GetFullPath(path).Replace('\\', '/');
}
