using Colossus.Infrastructure.DependencyInjection;
using Colossus.Infrastructure.Serialization;
using Colossus.Server.Configuration;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

// Dev stand-in for the static tile host: serves the baked tile tree as static immutable files and hosts
// the view registry API (see Controllers/ViewsController). In prod the SPA, this API, and the tiles each
// run on their own origin — the tiles from object storage + CDN (Cloudflare R2, see docs/DEPLOY.md) — so
// the browser fetches across subdomains; the CORS policy below is what permits that. All wiring is here;
// endpoints live in controllers.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ServerOptions>()
    .Bind(builder.Configuration.GetSection(ServerOptions.Section))
    .PostConfigure(o => o.ApplyEnvOverrides());
var server = new ServerOptions();
builder.Configuration.GetSection(ServerOptions.Section).Bind(server);
server.ApplyEnvOverrides();

builder.WebHost.UseUrls(server.ServerUrl);
builder.Services.AddColossus(builder.Configuration);
builder.Services.AddControllers().AddJsonOptions(o => ColossusJson.Apply(o.JsonSerializerOptions));
// Tiles + the view API are public, immutable, read-only data fetched cross-origin from the SPA's own
// subdomain. Any origin, and expose the range headers so a ranged facts.pack read (companion-scale R1/R5)
// is fully inspectable cross-origin. No credentials, so AllowAnyOrigin is safe.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("Content-Range", "Accept-Ranges", "Content-Length")));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info = new OpenApiInfo
    {
        Title = "Colossus Server API",
        Version = "v1",
        Description = "Serves baked tile pyramids as immutable static files and a read/write registry of view configs.",
    };
    return Task.CompletedTask;
}));

var app = builder.Build();
app.UseCors();

// A relative TilesRoot (e.g. "tiles" from appsettings) resolves under the app's content root, so a
// published deploy needs no absolute path. When tiles are served from a CDN (client TILES_BASE points
// elsewhere) this local handler simply serves whatever is present locally — often nothing.
if (!Path.IsPathRooted(server.TilesRoot))
    server.TilesRoot = Path.Combine(app.Environment.ContentRootPath, server.TilesRoot);

// /api/views (the dataset picker) reads view configs from a "views" folder. A published deploy has no
// repo marker for RepoPaths to find, so point it at a "views" folder shipped next to the app when one is
// present. Dev keeps RepoPaths' repo-root resolution (no such folder beside the project). The map itself
// never needs this — every view's config is embedded in its manifest.
var deployedViews = Path.Combine(app.Environment.ContentRootPath, "views");
if (Environment.GetEnvironmentVariable("COLOSSUS_VIEWS_DIR") is null && Directory.Exists(deployedViews))
    Environment.SetEnvironmentVariable("COLOSSUS_VIEWS_DIR", deployedViews);

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "Colossus v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "Colossus Server API";
});

Directory.CreateDirectory(server.TilesRoot);
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".arrow"] = "application/vnd.apache.arrow.stream";
// Companion pack (R2): gzip blocks range-read per tile. Static files honor Range natively; the
// compression lives inside the archive, so no Content-Encoding here.
contentTypes.Mappings[".pack"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(server.TilesRoot),
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

// Serve the built web app from wwwroot (SPA) and the showcase from wwwroot/showcase. A backend-only
// deploy has no wwwroot/index.html, so these are inert there and the "/" banner below answers instead.
app.UseDefaultFiles();
app.UseStaticFiles();

// Explicit routing AFTER the static handlers: WebApplication otherwise auto-inserts routing at the very
// start, which pre-matches the catch-all fallback (and "/") and makes StaticFileMiddleware skip real
// files. With routing here, actual wwwroot files win and only unmatched paths reach the endpoints.
app.UseRouting();

app.MapControllers();

// SPA deep-links (/?view=…) are query-based, but a hard fallback keeps any path route resolving to the
// app shell. Only fires when wwwroot/index.html exists; API routes above always win.
app.MapFallbackToFile("index.html");

app.MapGet("/", () => Results.Text(
    "Colossus server.\n" +
    "  GET  /swagger                      API explorer (Swagger UI)\n" +
    "  GET  /openapi/v1.json              OpenAPI document\n" +
    "  GET  /tiles/<viewId>/latest.json   baked tiles (static, immutable)\n" +
    "  GET  /api/views                    list registered views\n" +
    "  GET  /api/views/{id}               view config\n" +
    "  GET  /api/views/{id}/url           URL that renders the view\n" +
    "  POST /api/views                    register a view config\n", "text/plain"))
    .ExcludeFromDescription();

Console.WriteLine($"Serving tiles from {server.TilesRoot}; Swagger UI → /swagger; view URLs → {server.WebBaseUrl}");
app.Run();
