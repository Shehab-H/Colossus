using Colossus.Application;
using Colossus.Infrastructure;
using Colossus.Infrastructure.Baking;
using Colossus.Infrastructure.ClickHouse;
using Colossus.Infrastructure.Reduction;
using Colossus.Infrastructure.Sources;
using Colossus.Infrastructure.Tiles;
using Colossus.Infrastructure.Views;

// Bakes registered views into tile pyramids. No args bakes every view; pass ids to bake a subset,
// e.g. `dotnet run --project src/Colossus.Bake -- geo-points`. `verify` checks the fidelity invariant.

var views = new ViewRegistry();
var store = new FileBakeStore();

if (args is ["verify", ..])
{
    var reports = await new VerifyFidelityUseCase(views, store, new ArrowTileReader()).VerifyAllAsync();
    bool pass = true;
    foreach (var r in reports)
    {
        Console.WriteLine(
            $"  [{(r.Passed ? "PASS" : "FAIL")}] {r.ViewId}: leafRows={r.LeafRows:N0} total={r.TotalPoints:N0} " +
            $"leaves={r.Leaves} internal={r.Internal} overBudget={r.OverBudget}" +
            (r.Message is null ? "" : $" — {r.Message}"));
        pass &= r.Passed;
    }
    Console.WriteLine(pass ? "\nFidelity: PASS" : "\nFidelity: FAIL");
    return pass ? 0 : 1;
}

var selected = args.Length > 0 ? args.Select(views.Get).ToArray() : views.All().ToArray();
if (selected.Length == 0)
{
    Console.WriteLine($"No views registered under {RepoPaths.ViewsDir}. See docs/VIEW_CONFIG.md.");
    return 0;
}

using var clickHouse = new ClickHouseClient();
await clickHouse.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));

var bake = new BakeViewUseCase(new SourceAdapterCatalog(clickHouse), new ReductionCatalog(), store);
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
