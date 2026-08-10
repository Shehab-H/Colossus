namespace Colossus.Server.Configuration;

/// <summary>Server URLs bound from appsettings. Filesystem locations live in the shared Storage section.</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";

    public string ServerUrl { get; set; } = "http://localhost:5174";
    public string WebBaseUrl { get; set; } = "http://localhost:5173";

    public void Normalize()
    {
        ServerUrl = ServerUrl.TrimEnd('/');
        WebBaseUrl = WebBaseUrl.TrimEnd('/');
    }

    public string ViewUrl(string viewId) => $"{WebBaseUrl}/maps/{Uri.EscapeDataString(viewId)}";
}
