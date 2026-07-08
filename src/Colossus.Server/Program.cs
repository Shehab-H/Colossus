using Colossus.Domain.Model;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Serialization;
using Colossus.Infrastructure.Views;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;

// Dev stand-in for nginx: serve the baked tile tree as static immutable files, plus a small read/write
// API over the view registry (list / get / url / upload).

string tilesRoot = RepoPaths.TilesDir;
Directory.CreateDirectory(tilesRoot);
string webBase = (Environment.GetEnvironmentVariable("COLOSSUS_WEB_URL") ?? "http://localhost:5173").TrimEnd('/');
var views = new ViewRegistry();

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("COLOSSUS_SERVER_URL") ?? "http://localhost:5174");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
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

app.MapOpenApi();
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "Colossus v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "Colossus Server API";
});

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".arrow"] = "application/vnd.apache.arrow.stream";

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

string ViewUrl(string id) => $"{webBase}/?view={Uri.EscapeDataString(id)}";
bool HasBake(string id) => File.Exists(Path.Combine(tilesRoot, id, "latest.json"));
IResult Json<T>(T value) => Results.Text(ColossusJson.Serialize(value), "application/json");

app.MapGet("/api/views", () => Json(views.All().Select(v => new
{
    id = v.Id,
    title = v.Title,
    viewport = v.Viewport,
    mark = v.Mark,
    reduction = v.Reduction,
    url = ViewUrl(v.Id),
    baked = HasBake(v.Id),
})))
    .WithTags("Views")
    .WithSummary("List registered views")
    .WithDescription("Every view config in the registry with its render URL and whether tiles have been baked.");

app.MapGet("/api/views/{id}", (string id) =>
{
    try { return Json(views.Get(id)); }
    catch (ArgumentException e) { return Results.NotFound(new { error = e.Message }); }
})
    .WithTags("Views")
    .WithSummary("Get a view config")
    .WithDescription("The full canonical view descriptor: viewport, mark, reduction, source query, geometry, and channels.");

app.MapGet("/api/views/{id}/url", (string id) =>
{
    try
    {
        var view = views.Get(id);
        return Json(new { id = view.Id, url = ViewUrl(view.Id), baked = HasBake(view.Id) });
    }
    catch (ArgumentException e) { return Results.NotFound(new { error = e.Message }); }
})
    .WithTags("Views")
    .WithSummary("Get a view's render URL")
    .WithDescription("Resolves a view id to the frontend URL that renders it.");

app.MapPost("/api/views", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    string body = await reader.ReadToEndAsync();
    try
    {
        var view = ColossusJson.Deserialize<ViewConfig>(body);
        string path = views.Save(view);
        return Json(new { id = view.Id, url = ViewUrl(view.Id), saved = path });
    }
    catch (Exception e) { return Results.BadRequest(new { error = e.Message }); }
})
    .WithTags("Views")
    .WithSummary("Register a view config")
    .WithDescription("Validates and persists a view descriptor to the registry. Body is a canonical view config (see docs/VIEW_CONFIG.md).")
    .Accepts<ViewConfig>("application/json");

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

Console.WriteLine($"Serving tiles from {tilesRoot}; Swagger UI → /swagger; view URLs → {webBase}");
app.Run();
