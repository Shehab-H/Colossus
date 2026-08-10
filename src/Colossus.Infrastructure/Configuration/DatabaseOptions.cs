namespace Colossus.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public const string ConnectionStringName = "Database";

    public int CommandTimeoutSeconds { get; set; } = 30;
}
