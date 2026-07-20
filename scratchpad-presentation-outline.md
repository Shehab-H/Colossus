# Colossus — Technical Introduction (Code-Grounded Deck Outline)

**Audience:** mixed technical team (engineers + PM/design)
**Length:** ~18 slides, deep-dive
**Principle:** every claim points at a real file + function. Open the code live where you can.

> Legend: `path:line` references are clickable in the repo. Snippets are copied verbatim (trimmed).

---

## Section A — Orientation (slides 1–3)

### Slide 1 — Title
- **Colossus** — a code tour.
- "Render 10M–100M+ raw marks with zero aggregation, over one engine."
- We'll follow a row of data from a SQL query all the way to a GPU vertex.

### Slide 2 — The repo at a glance
- Backend: 4 .NET projects, dependencies point inward.
  - `Colossus.Domain` — models + **ports** (interfaces), zero I/O.
  - `Colossus.Application` — use cases (`BakeViewUseCase`, `BakePlanner`, `VerifyFidelityUseCase`).
  - `Colossus.Infrastructure` — adapters (ClickHouse, DuckDB, Arrow writers, file store).
  - `Colossus.Bake` / `Colossus.Server` — thin hosts.
- Frontend: `web/src/lib/*` — one config-driven render path (deck.gl + MapLibre).
- **Talking point:** you can name the layer a change belongs in before you open a file.

### Slide 3 — The one line the whole system is
- `source query → bake → Arrow IPC tiles → static serve → deck.gl binary → GPU`
- Two entrypoints, both thin:
  - `src/Colossus.Bake/Program.cs` — bake a view / `verify`.
  - `src/Colossus.Server/Program.cs` — serve tiles + view API.
- The rest of the deck walks this line left → right.

---

## Section B — The Source Seam (slides 4–5)

### Slide 4 — A source is just a query behind one interface
- **File:** `src/Colossus.Domain/Sources/ISourceAdapter.cs`
```csharp
// The source seam: the only place that knows a source dialect. It probes bounds + count and
// extracts the canonical, spatially sorted staging table. Everything after the extract is source-agnostic.
public interface ISourceAdapter
{
    Task<SourceBounds> ProbeAsync(ViewConfig view, CancellationToken ct = default);
    Task ExtractAsync(ViewConfig view, Bbox bounds, string destinationParquet, CancellationToken ct = default);
}
```
- `SourceBounds(Bbox, Count, DistinctGeometries)` — rows vs. distinct *shapes*. That ratio drives planning (next slide but one).
- ClickHouse is the only adapter today; Postgres/warehouse/files are new classes, not new pipelines.

### Slide 5 — Geometry normalization is a plugin switch
- **File:** `src/Colossus.Infrastructure/ClickHouse/Geometry/GeometrySqlFactory.cs`
```csharp
private static IGeometrySql Resolve(GeometryKind kind) => kind switch
{
    GeometryKind.Xy or GeometryKind.LonLat => new PointGeometrySql(),
    GeometryKind.Quadkey                    => new QuadkeyGeometrySql(),
    GeometryKind.Wkt                        => new WktGeometrySql(),
    _ => throw new NotSupportedException(...),
};
```
- `IGeometrySql` normalizes any source geometry into canonical `(x, y[, geometry, part_offsets])`.
- **The takeaway:** adding H3 or geohash = one more `case` + one class. Nothing downstream of extract changes.

---

## Section C — The Bake: Where the Work Happens (slides 6–8)

### Slide 6 — The reduction is chosen from data shape, not the chart name
- **File:** `src/Colossus.Application/BakePlanner.cs` → `SelectReduction`
```csharp
if (probe.Count <= _tilePointBudget)
    return ReductionKind.RawPassthrough;            // fits in one tile → ship as-is

bool areaMark = view.Mark is Mark.Polygon or Mark.Rect or Mark.Heat;
double rowsPerShape = (double)probe.Count / probe.DistinctGeometries;

if (areaMark || rowsPerShape >= CubeRowsPerShape)   // few shapes, many facts → aggregate
    return ReductionKind.Aggregate;

return ReductionKind.QuadtreeLod;                   // genuine point cloud → spatial pyramid
```
- No `if (view.type == "map")` anywhere. The planner reads the probe.
- `DefaultTilePointBudget = 250_000` marks per leaf tile; depth cap comes from distinct-shape count.

### Slide 7 — One reduction interface, swappable strategies
- **File:** `src/Colossus.Domain/Reduction/IReductionStrategy.cs`
```csharp
// Turns a sorted staging table into tiles. One implementation per ReductionKind;
// a strategy chooses which real rows land in which tile — never the schema, never the mark.
public interface IReductionStrategy
{
    ReductionKind Kind { get; }
    ReductionResult Reduce(ReductionContext context);
}
```
- Implementations: `RawPassthroughReducer`, `QuadtreeLodReducer`, `AggregateReducer` (in `Infrastructure/Reduction/`).
- Resolved by `ReductionCatalog` — dispatch, never a hardcoded branch.
- **Key line in the comment:** "which real rows land in which tile — never the schema, never the mark." That's the fidelity contract, in code.

### Slide 8 — The canonical schema is a single C# authority
- **File:** `src/Colossus.Domain/Tiling/TileSchema.cs`
```csharp
public static class TileSchema
{
    public const string X = "x";           // representative point — drives sort/LOD/query
    public const string Y = "y";
    public const string Geometry = "geometry";       // absent for points
    public const string PartOffsets = "part_offsets";
    public const string Triangles = "triangles";     // bake-time, tile-global — client never tessellates
    public const int    GridPerTile = 512;           // bake & client share this one value
}
```
- Referenced through the constant everywhere (adapter emit, Arrow writer, client read) — never a string literal.
- A rename is one edit + a compile error, not a silent runtime break.
- Mirror on the client: `web/src/lib/schema.ts` (`TileColumns`, `GRID_PER_TILE`).

---

## Section D — The Two Cross-Language Authorities (slides 9–10)

### Slide 9 — Tile math: the C# source of truth
- **File:** `src/Colossus.Domain/Tiling/TileMath.cs`
```csharp
// Data-space coordinate of grid line i of n equal divisions of [min, max].
// n is a power of two, so (max-min)/n is exact and a tile's max edge == its neighbour's min edge,
// bit for bit — a point on a seam lands in exactly one tile, never two, never zero.
public static double Edge(double min, double max, long n, long i) =>
    i >= n ? max : min + i * ((max - min) / n);
```
- The whole tiling scheme (edges, `CellIndex`, `Contains`, `PointToTile`) lives here.
- `TileSql.cs` renders the *same* math as DuckDB SQL for the reducers.

### Slide 10 — …and its TypeScript mirror, pinned by a shared fixture
- **File:** `web/src/lib/tiling.ts` → `pointToTile`
```ts
/** Point → tile index at zoom z — the forward map and mirror of the C# tiling authority
 *  (TileMath.PointToTile / TileSql). Pinned to it by tiling.test.ts against the shared fixture. */
export function pointToTile(root, z, px, py): [number, number] { ... }
```
- Contract kept honest by `tests/fixtures/tiling-cases.json`: the C# `TilingConformanceTests` **and** the web `tiling.test.ts` both run it.
- Change the scheme → regenerate the fixture → both languages must still pass. This is how the system doesn't silently drift.

---

## Section E — Serve & the Client Render Path (slides 11–13)

### Slide 11 — Tiles flow network → worker → GPU with no copy
- **File:** `web/src/lib/tileData.ts` → the `TileData` interface
```ts
// Format 2 only: the single fetched ArrayBuffer every heavy column is a view into. Held as the
// retention anchor (and transferred once) so the tile's bytes flow network → worker → GPU with no copy.
buffer?: ArrayBuffer;
polyTriangles?: Uint32Array;  // bake-time triangle indices — deck skips earcut entirely
```
- Arrow's column buffers *are* the typed arrays deck.gl wants — `tableFromIPC` is essentially a memcpy.
- Strings never cross the worker boundary as JS objects: categorical columns become `{codes, dict}` (see `DictColumn`) — that cloning was the old zoom stutter.

### Slide 12 — Which tiles to draw is pure data-space math
- **File:** `web/src/lib/tiling.ts` → `selectTiles`
```ts
// Quadtree LOD + culling: descend from the root, keep tiles that intersect the viewport and are
// leaves or small enough on screen. Identical for every mark and viewport.
const screenPx = (r.cw / vbSpanX) * vb.widthPx;
if (meta.isLeaf || screenPx <= targetPx) { chosen.push(tileKey(z, x, y)); return; }
for (let q = 0; q < 4; q++) visit(z + 1, x*2 + (q&1), y*2 + ((q>>1)&1));
```
- Companion functions in the same file: `coverTiles` (what to draw while loading — parent covers children, single-frame swap) and `prefetchCandidates` (warm parents/ring/children during idle).
- **One code path** draws map, scatter, choropleth — the viewport is just numbers.

### Slide 13 — Recolor = a texture upload, not a data touch
- **File:** `web/src/lib/colorScaleExtension.ts` — a deck `LayerExtension`
```ts
// A LayerExtension that colors marks on the GPU from a lookup-table texture. The per-mark `scaleValue`
// attribute uploads once per (tile, channel); a recolor swaps the LUT texture + a few uniforms,
// touching no per-mark data.
'vs:DECKGL_FILTER_COLOR': `
  colorScale_t = clamp((colorScale_v - colorScale.domain.x) / (domain.y - domain.x), 0.0, 1.0);
  color.rgb = texture(colorScaleLut, vec2(colorScale_t, 0.5)).rgb;   // color chosen in the vertex shader
`
```
- The value→color transform mirrors the CPU `colorScale.ts` (baked into the LUT by `colorLut.ts`) — the GPU can't disagree with the legend.
- Changing scale / theme / channel = swap a ~KB texture. No per-mark array moves.

---

## Section F — Interaction Is GPU State (slide 14)

### Slide 14 — A filter change is a uniform update, zero bytes moved
- **File:** `web/src/lib/gpuFilter.ts`
```ts
// GPU filtering: every filter is a numeric range on one float slot of a DataFilterExtension. A filter
// change is then a uniform update (filterRange/filterEnabled) — no fetch, decode, or re-upload.
export function filterRanges(slots, filters): [number, number][] {
  // dimension equality → [code, code]; date range → [dayFrom, dayTo]; (all) → wide open
}
```
- Slot values baked into the tile **once** at decode (`buildFilterValues`, per-mark for points / per-vertex for polygons); ranges recomputed per filter change on the main thread.
- **The invariant, stated in the code:** `(version, tileKey)` is the only data identity. Filter, color, and measure are GPU state — never a reason to refetch. Say this line and let it land.

---

## Section G — Chart-Type-as-Config & Embeds (slides 15–16)

### Slide 15 — A view is a JSON file, not code
- A view = `viewport + mark + channel mapping + reduction + source`. Drop `views/<id>.json`, bake, live.
- Show a real minimal view (row regime):
```json
{
  "id": "geo-points", "viewport": "geo", "mark": "point",
  "source": {
    "adapter": "clickhouse",
    "query": "SELECT lon, lat, value, category FROM colossus.points_geo",
    "geometry": { "kind": "lonLat", "lon": "lon", "lat": "lat" },
    "channels": [
      { "name": "value", "column": "value", "role": "measure", "type": "f32" },
      { "name": "category", "column": "category", "role": "dimension", "type": "dict" }
    ]
  }
}
```
- Loaded by `Colossus.Infrastructure/Views/ViewRegistry.cs` + `web/src/lib/views.ts`. No redeploy to add a visualization.

### Slide 16 — Every map is fully described by its URL
- `web/src/lib/embed.ts` round-trips view state ↔ query params.
- `/?embed=1&view=ookla-fixed&color=download_mbps&scale=quantize&bins=6&theme=light&lng=10&lat=50&z=3.6`
- An `<iframe>` reproduces the map exactly; the HUD's **Embed** button copies the snippet.
- **Live demo:** open the map, change `&scale=` / `&theme=`, watch it recolor with no refetch (ties back to slide 13).

---

## Section H — The Active Frontier & Wrap (slides 17–18)

### Slide 17 — Companion-scale: where the current work is
- Group-regime views fold per-mark **measures** over baked fact partials under the active filter context.
- The scaling problem + the code that attacks it:
  - **Slab format** — `src/Colossus.Infrastructure/Tiles/SlabCompanionWriter.cs` + `web/src/lib/slab.ts` (indexed planes, key columns gone).
  - **Remote fold** (built) — `POST /api/views/{id}/fold` in `Colossus.Server/Controllers/FoldController.cs`, executed by `Fold/DuckDbFoldExecutor.cs`; client route in `web/src/lib/remoteFold.ts`.
  - **Context-sliced fetch** — fetch only the cell rows the active selection reads.
- Cross-language parity: remote fold == client fold **byte-for-byte**, pinned by a shared fixture — same discipline as the tiling authority.

### Slide 18 — Takeaways (each is a place in the code)
- **Seams, not pipelines** — new source / geometry / reduction = a new class at an interface (`ISourceAdapter`, `IGeometrySql`, `IReductionStrategy`).
- **One schema, one render path** — `TileSchema.cs` ↔ `schema.ts`; `selectTiles` draws everything.
- **Authorities pinned across languages** — `TileMath.cs` ↔ `tiling.ts`, fold parity, geometry codec, measures — each a shared fixture.
- **Interaction is GPU state** — `gpuFilter.ts` + `colorScaleExtension.ts`; `(version, tileKey)` is the only identity.
- Q&A — open the repo and follow a request live.

---

## Optional backup / live-demo slides
- **Run it end to end:** `dotnet run --project src/Colossus.Bake -- <view-id>` → `dotnet run --project src/Colossus.Server` → `cd web && npm run dev`.
- **Prove fidelity:** `dotnet run --project src/Colossus.Bake -- verify` (asserts `Σ tiles == source`).
- **Tests as a map of the system:** `dotnet test` (bake logic + cross-language conformance), `web/ npm run test` (tiling mirror, cover/select).
- **Composition root:** `Colossus.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs` (`AddColossus`) — the one place Application meets Infrastructure.
