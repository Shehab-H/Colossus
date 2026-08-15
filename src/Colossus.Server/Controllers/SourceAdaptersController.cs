using Colossus.Application.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Colossus.Server.Controllers;

[ApiController]
[Route("api/source-adapters")]
[Tags("Source adapters")]
public sealed class SourceAdaptersController(SourcePreviewService sources) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(sources.ListAdapters());
}

[ApiController]
[Tags("Source preview")]
public sealed class SourcePreviewController(SourcePreviewService sources) : ControllerBase
{
    [HttpPost("api/connections/{connectionId:guid}/test")]
    public async Task<IActionResult> ValidateConnection(Guid connectionId, CancellationToken ct)
    {
        try { return Ok(await sources.ValidateConnectionAsync(connectionId, ct)); }
        catch (Exception error) { return this.ToResponse(error); }
    }

    [HttpGet("api/connections/{connectionId:guid}/locations")]
    public async Task<IActionResult> Discover(Guid connectionId, CancellationToken ct)
    {
        try { return Ok(await sources.DiscoverAsync(connectionId, ct)); }
        catch (Exception error) { return this.ToResponse(error); }
    }

    [HttpPost("api/datasets/{datasetId:guid}/preview")]
    public async Task<IActionResult> Preview(Guid datasetId, CancellationToken ct)
    {
        try { return Ok(await sources.PreviewDatasetAsync(datasetId, ct: ct)); }
        catch (Exception error) { return this.ToResponse(error); }
    }
}
