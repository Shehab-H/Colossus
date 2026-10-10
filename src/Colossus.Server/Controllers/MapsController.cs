using Colossus.Application.Catalog;
using Colossus.Application.Abstractions.Catalog;
using Colossus.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Colossus.Server.Controllers;

public sealed record BakePolicyRequest(
    bool Enabled,
    TimeSpan? Interval,
    string TimeZoneId = "UTC",
    bool AllowOverlap = false,
    int RetainSuccessfulReleases = 3,
    TimeSpan? RetainSuccessfulFor = null);

[ApiController]
[Route("api/maps")]
[Tags("Maps")]
public sealed class MapsController(
    ProductCatalogService catalog,
    MapManagementService management,
    BakeSchedulingService scheduling,
    IReleaseRepository releases,
    ReleaseMaintenanceService maintenance) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeArchived, CancellationToken ct) =>
        Ok(await management.ListAsync(includeArchived, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try { return Ok(await management.GetAsync(id, ct)); }
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

    [HttpGet("{id:guid}/runs")]
    public async Task<IActionResult> ListRuns(
        Guid id,
        [FromQuery] BakeRunStatus[]? status,
        [FromQuery] string? cursor,
        [FromQuery] int limit = BakeRunPageRequest.DefaultLimit,
        CancellationToken ct = default)
    {
        try
        {
            var request = new BakeRunPageRequest(
                limit, status ?? [], string.IsNullOrEmpty(cursor) ? null : BakeRunCursor.Decode(cursor));
            return Ok(await management.ListRunsAsync(id, request, ct));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost("{id:guid}/runs")]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        try { return Accepted(MapManagementService.DescribeRun(await scheduling.RunNowAsync(id, ct))); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost("{id:guid}/runs/{runId:guid}/cancel")]
    public async Task<IActionResult> CancelRun(Guid id, Guid runId, CancellationToken ct)
    {
        try { return Ok(MapManagementService.DescribeRun(await scheduling.CancelAsync(id, runId, ct))); }
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
                request.RetainSuccessfulReleases, Revision: 0, UpdatedAt: default,
                request.RetainSuccessfulFor);
            return Ok(await scheduling.SetPolicyAsync(policy, expectedRevision, ct));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPut("{id:guid}/releases/{releaseId:guid}/pin")]
    public async Task<IActionResult> PinRelease(
        Guid id, Guid releaseId, [FromQuery] bool pinned = true, CancellationToken ct = default)
    {
        try
        {
            _ = await catalog.GetMapAsync(id, ct);
            var release = (await releases.ListAllForMapAsync(id, ct))
                .FirstOrDefault(value => value.Id == releaseId)
                ?? throw new CatalogNotFoundException($"Unknown release '{releaseId}'.");
            if (release.State == ReleaseState.Deleted)
                throw new CatalogConflictException("A deleted release cannot be pinned.");
            _ = await releases.UpdateAsync(release with { Pinned = pinned }, ct);
            return Ok((await management.GetAsync(id, ct)).Releases.Single(value => value.Id == releaseId));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost("{id:guid}/releases/reconcile")]
    public async Task<IActionResult> ReconcileReleases(Guid id, CancellationToken ct)
    {
        try
        {
            var map = await catalog.GetMapAsync(id, ct);
            var policy = await catalog.GetBakePolicyAsync(id, ct);
            return Ok(await maintenance.ReconcileMapAsync(
                id,
                map.Draft.Slug,
                policy?.RetainSuccessfulReleases ?? 3,
                policy?.RetainSuccessfulFor,
                ct));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }
}
