using Colossus.Application;
using Colossus.Domain.Baking;
using Colossus.Infrastructure;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Bakes registered views into tile pyramids. No args bakes every view; pass ids to bake a subset,
// e.g. `dotnet run --project src/Colossus.Bake -- geo-points`. `verify` checks the fidelity invariant.
// The service graph is the same AddColossus() the server uses — wired in exactly one place.

// Build without command-line config so bare args (view ids / "verify") aren't parsed as switches;
// ClickHouse + path config still come from env/appsettings via AddColossus.
var builder = Host.CreateApplicationBuilder();
builder.Services.AddColossus(builder.Configuration);
using var host = builder.Build();
var services = host.Services;

var views = services.GetRequiredService<IViewCatalog>();

if (args is ["verify", ..])
{
    var reports = await services.GetRequiredService<VerifyFidelityUseCase>().VerifyAllAsync();
    bool ok = true;
    foreach (var r in reports)
    {
        Console.WriteLine(
            $"  [{(r.Passed ? "PASS" : "FAIL")}] {r.ViewId}: leafRows={r.LeafRows:N0} " +
            $"source={(r.SourceRows is { } s ? s.ToString("N0") : "?")} total={r.TotalPoints:N0} " +
            $"leaves={r.Leaves} internal={r.Internal} overBudget={r.OverBudget}" +
            (r.Message is null ? "" : $" — {r.Message}"));
        ok &= r.Passed;
    }
    Console.WriteLine(ok ? "\nFidelity: PASS" : "\nFidelity: FAIL");
    return ok ? 0 : 1;
}

var selected = args.Length > 0 ? args.Select(views.Get).ToArray() : views.All().ToArray();
if (selected.Length == 0)
{
    Console.WriteLine($"No views registered under {RepoPaths.ViewsDir}. See docs/VIEW_CONFIG.md.");
    return 0;
}

var clickHouse = services.GetRequiredService<ClickHouseClient>();
await clickHouse.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));

var bake = services.GetRequiredService<BakeViewUseCase>();
foreach (var view in selected)
{
    var outcome = await bake.BakeAsync(view);
    Console.WriteLine(outcome.IsEmpty
        ? $"  {outcome.ViewId}: empty source — skipped"
        : $"  {outcome.ViewId} → {outcome.Version}: {outcome.SourceRows:N0} rows, " +
          $"{outcome.TileCount} tiles ({outcome.LeafCount} leaves), maxZoom={outcome.MaxZoom}");
}

Console.WriteLine("\nBake complete.");
return 0;
