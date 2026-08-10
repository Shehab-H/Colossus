using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Colossus.Server.Controllers;

public sealed record CreateConnectionRequest(
    ConnectionDraft Definition,
    IReadOnlyDictionary<string, string>? Secrets = null);

public sealed record ConnectionResponse(
    Guid Id,
    string WorkspaceKey,
    string Slug,
    string Name,
    string Adapter,
    long Revision,
    bool Archived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ConnectionResponse From(CatalogConnection connection) => new(
        connection.Id,
        connection.WorkspaceKey,
        connection.Draft.Slug,
        connection.Draft.Name,
        connection.Draft.Adapter,
        connection.Revision,
        connection.Archived,
        connection.CreatedAt,
        connection.UpdatedAt);
}

[ApiController]
[Route("api/connections")]
[Tags("Connections")]
public sealed class ConnectionsController(ProductCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await catalog.ListConnectionsAsync(ct: ct)).Select(ConnectionResponse.From));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try { return Ok(ConnectionResponse.From(await catalog.GetConnectionAsync(id, ct))); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateConnectionRequest request, CancellationToken ct)
    {
        try
        {
            var connection = await catalog.CreateConnectionAsync(request.Definition, request.Secrets, ct: ct);
            return CreatedAtAction(nameof(Get), new { id = connection.Id }, ConnectionResponse.From(connection));
        }
        catch (Exception e) { return this.ToResponse(e); }
    }
}
