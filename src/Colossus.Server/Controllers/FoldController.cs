using Colossus.Domain.Model;
using Colossus.Infrastructure.Fold;
using Colossus.Infrastructure.Serialization;
using Colossus.Server.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Colossus.Server.Controllers;

/// <summary>The R4 remote fold endpoint (companion-scale REQUIREMENTS.md R4). Additive to the static tile
/// serve (RULES R7 untouched): it folds a view's measures on the server over the version's BAKED facts
/// Parquet (RULES R5 — never the source DB) and returns per-tile, mki-keyed Arrow columns behind the same
/// <c>fold(measures, context) → columns</c> seam the client fold serves. Only over-budget views (priced
/// remote at bake) route here; the tiles themselves keep streaming as immutable static files.</summary>
[ApiController]
[Route("api/views")]
[Tags("Views")]
public sealed class FoldController(
    DuckDbFoldExecutor executor, IOptions<ServerOptions> options, IWebHostEnvironment env) : ControllerBase
{
    private readonly ServerOptions _server = options.Value;

    [HttpPost("{id}/fold")]
    public async Task<IActionResult> Fold(string id, [FromBody] FoldQuery query, CancellationToken ct)
    {
        if (query.Tiles.Count == 0 || query.Measures.Count == 0)
            return BadRequest(new { error = "fold requires at least one tile and one measure" });

        // Resolve the tiles root exactly as the static handler does (a relative root sits under the app's
        // content root), so the fold reads from the same version tree the server serves.
        string tilesRoot = Path.IsPathRooted(_server.TilesRoot)
            ? _server.TilesRoot
            : Path.Combine(env.ContentRootPath, _server.TilesRoot);

        string? version = query.Version ?? ReadLatestVersion(tilesRoot, id);
        if (version is null) return NotFound(new { error = $"no baked version for '{id}'" });

        string versionDir = Path.Combine(tilesRoot, id, version);
        string manifestPath = Path.Combine(versionDir, "manifest.json");
        if (!System.IO.File.Exists(manifestPath)) return NotFound(new { error = $"no manifest for '{id}' {version}" });

        var manifest = ColossusJson.Deserialize<Manifest>(await System.IO.File.ReadAllTextAsync(manifestPath, ct));
        if (manifest.FactsParquet is null)
            return BadRequest(new { error = $"'{id}' has no retained facts Parquet — not a group-regime R4 bake" });

        string factsPath = Path.Combine(versionDir, manifest.FactsParquet);
        if (!System.IO.File.Exists(factsPath))
            return NotFound(new { error = "facts Parquet missing on the server (remote-fold views ship it beside the app)" });

        try
        {
            // DuckDB is blocking; keep it off the request thread so the server stays responsive.
            var result = await Task.Run(() => executor.Fold(manifest, factsPath, query.Measures, query.Context, query.Tiles), ct);
            Response.Headers["X-Fold-Ms"] = result.FoldMs.ToString();
            return File(result.Arrow, "application/vnd.apache.arrow.stream");
        }
        catch (ArgumentException e)
        {
            return BadRequest(new { error = e.Message });
        }
    }

    private static string? ReadLatestVersion(string tilesRoot, string id)
    {
        string path = Path.Combine(tilesRoot, id, "latest.json");
        return System.IO.File.Exists(path)
            ? ColossusJson.Deserialize<LatestPointer>(System.IO.File.ReadAllText(path)).Version
            : null;
    }
}
