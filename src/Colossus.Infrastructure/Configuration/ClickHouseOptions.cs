namespace Colossus.Infrastructure.Configuration;

/// <summary>Connection settings for the ClickHouse HTTP interface, bound from appsettings.</summary>
public sealed class ClickHouseOptions
{
    public const string Section = "ClickHouse";

    public string BaseUrl { get; set; } = "http://localhost:8123";
    public string User { get; set; } = "colossus";
    public string Password { get; set; } = "colossus";
}
