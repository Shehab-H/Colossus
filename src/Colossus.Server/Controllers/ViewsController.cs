using Colossus.Domain.Baking;
using Colossus.Domain.Model;
using Colossus.Server.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Colossus.Server.Controllers;


[ApiController]
[Route("api/views")]
[Tags("Views")]
public sealed class ViewsController(IViewCatalog views, IOptions<ServerOptions> options) : ControllerBase
{
    private readonly ServerOptions _server = options.Value;

    [HttpGet]
    public IActionResult List() => Ok(views.All().Select(Summarize));

    [HttpGet("{id}")]
    public IActionResult Get(string id) =>
        Found(id, view => Ok(view));

    [HttpGet("{id}/url")]
    public IActionResult GetUrl(string id) =>
        Found(id, view => Ok(new { id = view.Id, url = _server.ViewUrl(view.Id), baked = _server.HasBake(view.Id) }));

    [HttpPost]
    public IActionResult Register([FromBody] ViewConfig view)
    {
        try
        {
            string path = views.Save(view);
            return Ok(new { id = view.Id, url = _server.ViewUrl(view.Id), saved = path });
        }
        catch (Exception e)
        {
            return BadRequest(new { error = e.Message });
        }
    }

    private IActionResult Found(string id, Func<ViewConfig, IActionResult> onFound)
    {
        try { return onFound(views.Get(id)); }
        catch (ArgumentException e) { return NotFound(new { error = e.Message }); }
    }

    private object Summarize(ViewConfig v) => new
    {
        id = v.Id,
        title = v.Title,
        viewport = v.Viewport,
        mark = v.Mark,
        reduction = v.Reduction,
        url = _server.ViewUrl(v.Id),
        baked = _server.HasBake(v.Id),
    };
}
