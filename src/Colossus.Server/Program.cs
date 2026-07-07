using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// Colossus.Server — the dev stand-in for nginx: serve the baked tile tree as static, immutable files.
// Prod swaps this for nginx with identical cache headers (docs/PLAN.md, M6). No compute, no DB.

string tilesRoot = Colossus.Core.RepoPaths.TilesDir;
Directory.CreateDirectory(tilesRoot);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("COLOSSUS_SERVER_URL") ?? "http://localhost:5174");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// .arrow tiles served as binary; pointers/manifests get a short TTL, everything else is immutable.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".arrow"] = "application/vnd.apache.arrow.file";

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(tilesRoot),
    RequestPath = "/tiles",
    ContentTypeProvider = contentTypes,
    ServeUnknownFileTypes = true,
    OnPrepareResponse = ctx =>
    {
        string name = ctx.File.Name;
        ctx.Context.Response.Headers.CacheControl =
            name is "latest.json" or "manifest.json"
                ? "public, max-age=60"
                : "public, max-age=31536000, immutable";
    },
});

app.MapGet("/", () => Results.Text(
    $"Colossus tile server. Serving {tilesRoot} at /tiles/<viewId>/latest.json", "text/plain"));

Console.WriteLine($"Serving tiles from {tilesRoot}");
app.Run();
