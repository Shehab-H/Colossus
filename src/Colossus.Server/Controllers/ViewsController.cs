using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Colossus.Domain.Model;
using Colossus.Infrastructure.Configuration;
using Colossus.Server.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Colossus.Server.Controllers;

[ApiController]
[Route("api/views")]
[Tags("Views")]
public sealed class ViewsController(
    ProductCatalogService catalog,
    IOptions<ServerOptions> options,
    IOptions<StorageOptions> storageOptions) : ControllerBase
{
    private readonly ServerOptions _server = options.Value;
    private readonly StorageOptions _storage = storageOptions.Value;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await catalog.ListMapsAsync(ct: ct)).Select(Summarize));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await Found(id, view => Ok(view), ct);

    [HttpGet("{id}/url")]
    public async Task<IActionResult> GetUrl(string id, CancellationToken ct) =>
        await Found(id, view => Ok(new { id = view.Id, url = _server.ViewUrl(view.Id), baked = _storage.HasBake(view.Id) }), ct);

    [HttpPost]
    public async Task<IActionResult> Register([FromBody] ViewConfig view, CancellationToken ct)
    {
        try
        {
            var map = await catalog.ImportLegacyViewAsync(view, ct: ct);
            return Ok(new { id = view.Id, url = _server.ViewUrl(view.Id), saved = $"catalog://maps/{map.Id:D}" });
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    private async Task<IActionResult> Found(
        string id, Func<ViewConfig, IActionResult> onFound, CancellationToken ct)
    {
        try { return onFound(await catalog.ResolveSnapshotAsync(id, ct: ct)); }
        catch (CatalogNotFoundException e) { return NotFound(new { error = e.Message }); }
    }

    private object Summarize(CatalogMap map) => new
    {
        id = map.Draft.Slug,
        title = map.Draft.Title,
        viewport = map.Draft.Viewport,
        mark = map.Draft.Mark,
        reduction = map.Draft.Reduction,
        url = _server.ViewUrl(map.Draft.Slug),
        baked = _storage.HasBake(map.Draft.Slug),
    };
}
