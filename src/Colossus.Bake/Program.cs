using Colossus.Bake;
using Colossus.Core;

// Colossus.Bake — bakes one or more views into Arrow tile pyramids. With no args, bakes every view in
// the catalog; pass view ids to bake a subset, e.g. `dotnet run --project src/Colossus.Bake -- geo-points`.

string tilesRoot = RepoPaths.TilesDir;
string stagingRoot = RepoPaths.StagingDir;

// `verify` mode reads the baked tiles back and checks the no-simplification invariant, then exits.
if (args.Length > 0 && args[0] == "verify")
{
    bool ok = Verify.AllViews(tilesRoot);
    Console.WriteLine(ok ? "\nFidelity: PASS" : "\nFidelity: FAIL");
    return ok ? 0 : 1;
}

var views = args.Length > 0 ? args.Select(Views.ById).ToArray() : Views.All.ToArray();

using var ch = new ClickHouseClient();
await ch.WaitUntilReadyAsync(TimeSpan.FromMinutes(2));

foreach (var view in views)
    await BakePipeline.BakeAsync(ch, view, tilesRoot, stagingRoot);

Console.WriteLine("\nBake complete.");
return 0;
