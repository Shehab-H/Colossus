using Colossus.Application.Catalog;
using Colossus.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Colossus.Server.Controllers;

[ApiController]
[Route("api/datasets")]
[Tags("Datasets")]
public sealed class DatasetsController(ProductCatalogService catalog) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await catalog.ListDatasetsAsync(ct: ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        try { return Ok(await catalog.GetDatasetAsync(id, ct)); }
        catch (Exception e) { return this.ToResponse(e); }
    }

    [HttpPost]
    public async Task<IActionResult> Create(DatasetDraft definition, CancellationToken ct)
    {
        try
        {
            var dataset = await catalog.CreateDatasetAsync(definition, ct: ct);
            return CreatedAtAction(nameof(Get), new { id = dataset.Id }, dataset);
        }
        catch (Exception e) { return this.ToResponse(e); }
    }
}
