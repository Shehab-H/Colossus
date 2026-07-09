namespace Colossus.Infrastructure.Configuration;

/// <summary>Connection settings for the ClickHouse HTTP interface. Bound from the <c>ClickHouse</c>
/// config section, with the legacy <c>COLOSSUS_CH_*</c> environment variables taking precedence so the
/// docker-compose dev workflow keeps working.</summary>
public sealed class ClickHouseOptions
{
    public const string Section = "ClickHouse";

    public string BaseUrl { get; set; } = "http://localhost:8123";
    public string User { get; set; } = "colossus";
    public string Password { get; set; } = "colossus";
}
