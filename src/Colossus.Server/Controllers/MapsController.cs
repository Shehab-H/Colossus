using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Colossus.Server.Controllers;

public sealed record BakePolicyRequest(
    bool Enabled,
    TimeSpan? Interval,
    string TimeZoneId = "UTC",
    bool AllowOverlap = false,
    int RetainSuccessfulReleases = 3);

[ApiController]
[Route("api/maps")]
[Tags("Maps")]
public sealed class MapsController(ProductCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeArchived, CancellationToken ct) =>
        Ok(await catalog.ListMapsAsync(includeArchived: includeArchived, ct: ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try { return Ok(await catalog.GetMapAsync(id, ct)); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost]
    public async Task<IActionResult> Create(MapDraft definition, CancellationToken ct)
    {
        try
        {
            var map = await catalog.CreateMapAsync(definition, ct: ct);
            return CreatedAtAction(nameof(Get), new { id = map.Id }, map);
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id, [FromQuery] long expectedRevision, MapDraft definition, CancellationToken ct)
    {
        try { return Ok(await catalog.UpdateMapAsync(id, definition, expectedRevision, ct)); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Archive(Guid id, [FromQuery] long expectedRevision, CancellationToken ct)
    {
        try { return Ok(await catalog.ArchiveMapAsync(id, expectedRevision, ct)); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpGet("{id:guid}/bake-policy")]
    public async Task<IActionResult> GetBakePolicy(Guid id, CancellationToken ct)
    {
        try
        {
            _ = await catalog.GetMapAsync(id, ct);
            return await catalog.GetBakePolicyAsync(id, ct) is { } policy ? Ok(policy) : NotFound();
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPut("{id:guid}/bake-policy")]
    public async Task<IActionResult> SetBakePolicy(
        Guid id,
        [FromQuery] long? expectedRevision,
        BakePolicyRequest request,
        CancellationToken ct)
    {
        try
        {
            var policy = new BakePolicy(
                id, request.Enabled, request.Interval, request.TimeZoneId, request.AllowOverlap,
                request.RetainSuccessfulReleases, Revision: 0, UpdatedAt: default);
            return Ok(await catalog.SetBakePolicyAsync(policy, expectedRevision, ct));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }
}
