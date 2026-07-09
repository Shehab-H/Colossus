using Colossus.Infrastructure;

namespace Colossus.Server.Configuration;

/// <summary>Server-host settings: where tiles live on disk, the URL the tiles/API are served on, and
/// the web app's base URL used to build render deep-links. Bound from the <c>Server</c> config section,
/// with the legacy <c>COLOSSUS_*</c> environment variables taking precedence.</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";

    public string TilesRoot { get; set; } = RepoPaths.TilesDir;
    public string ServerUrl { get; set; } = "http://localhost:5174";
    public string WebBaseUrl { get; set; } = "http://localhost:5173";

    public void ApplyEnvOverrides()
    {
        ServerUrl = (Environment.GetEnvironmentVariable("COLOSSUS_SERVER_URL") ?? ServerUrl).TrimEnd('/');
        WebBaseUrl = (Environment.GetEnvironmentVariable("COLOSSUS_WEB_URL") ?? WebBaseUrl).TrimEnd('/');
        TilesRoot = Environment.GetEnvironmentVariable("COLOSSUS_TILES_DIR") ?? TilesRoot;
    }

    public string ViewUrl(string viewId) => $"{WebBaseUrl}/?view={Uri.EscapeDataString(viewId)}";

    public bool HasBake(string viewId) => File.Exists(Path.Combine(TilesRoot, viewId, "latest.json"));
}
