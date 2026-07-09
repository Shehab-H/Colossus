using Colossus.Infrastructure.DependencyInjection;
using Colossus.Infrastructure.Serialization;
using Colossus.Server.Configuration;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

// Dev stand-in for nginx: serves the baked tile tree as static immutable files and hosts the view
// registry API (see Controllers/ViewsController). Prod swaps to nginx for the static tiles, identical
// headers. All wiring is here; endpoints live in controllers.

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

Directory.CreateDirectory(server.TilesRoot);
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".arrow"] = "application/vnd.apache.arrow.stream";
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

app.MapControllers();

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
