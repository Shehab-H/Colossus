# Colossus — Architecture

How the code is organized and *why*, so a change lands in the right place. The invariants it must uphold
are in [RULES.md](RULES.md); the canonical config DSL (including the group/measure model, the active
direction) is [VIEW_CONFIG.md](VIEW_CONFIG.md); client perf architecture is in
[gpu-residency/](gpu-residency/README.md). This file is about structure.

## Layers (backend, .NET)

Dependencies point inward. Domain knows nothing of ClickHouse, DuckDB, ASP.NET, or the filesystem.

```
Colossus.Domain          models + ports (interfaces). No I/O, no dependencies.
   ▲
Colossus.Application      use cases: BakeViewUseCase, BakePlanner, VerifyFidelityUseCase.
   ▲                        Orchestrates ports; depends only on Domain.
Colossus.Infrastructure   adapters that implement the ports: ClickHouse source, DuckDB reducers,
   ▲                        Arrow tile writer, file store, view registry. Hosts the DI composition
   │                        root (AddColossus) — the one place Application meets Infrastructure.
Colossus.Bake  /  Colossus.Server   hosts. Thin: build config + DI, then delegate. The server's
                            endpoints live in Controllers/, never in Program.cs.
```

- **Ports** (Domain interfaces): `ISourceAdapter`, `IReductionStrategy`/`IReductionCatalog`,
  `IBakeStore`, `IViewCatalog`, `ITileReader`. Each has exactly one adapter in Infrastructure today and
  is swappable without touching the layers above.
- **Composition root**: [`AddColossus`](../src/Colossus.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)
  wires the whole graph. Both hosts call it, so wiring exists once. Config binds to typed options
  (`ClickHouseOptions`, `ServerOptions`) with the legacy `COLOSSUS_*` env vars taking precedence.

## The two authorities

Two contracts are implemented in more than one place/language. Each has **one source of truth** and a
**test that pins the copies to it** — this is what keeps the system from silently drifting.

1. **The tiling scheme** — "which tile does a point fall in at zoom z over this bbox."
   - Authority: [`TileMath`](../src/Colossus.Domain/Tiling/TileMath.cs) (C#).
   - SQL projection: [`TileSql`](../src/Colossus.Infrastructure/Tiling/TileSql.cs) renders the same math
     as DuckDB expressions for the reducers — the *only* place tile math becomes SQL.
   - Client mirror: [`web/src/lib/tiling.ts`](../web/src/lib/tiling.ts) `pointToTile`/`tileRect`.
   - Pinned by: [`tests/fixtures/tiling-cases.json`](../tests/fixtures/tiling-cases.json), verified by
     the C# `TilingConformanceTests` (both `TileMath` and `TileSql`-through-DuckDB) **and** the web
     `tiling.test.ts`. Change the scheme → regenerate the fixture → both sides must still pass.

2. **The canonical tile schema** — the column names every tile carries (RULES R3).
   - Authority: [`TileSchema`](../src/Colossus.Domain/Tiling/TileSchema.cs) (`x`, `y`, `geometry`,
     `part_offsets`, `triangles`, `id`, `GridPerTile`).
   - Client mirror: [`web/src/lib/schema.ts`](../web/src/lib/schema.ts) `TileColumns` / `GRID_PER_TILE`.
   - Referenced through the constant everywhere (adapter emit, Arrow writer, client read) — never a
     string literal — so a rename is one edit and a compile error, not a runtime break downstream.

## The plugin seams (where the system grows)

Adding capability means adding a small class at a seam, not editing a pipeline:

- **Source geometry** → an `IGeometrySql` in
  [`ClickHouse/Geometry/`](../src/Colossus.Infrastructure/ClickHouse/Geometry) selected by
  `GeometrySqlFactory`. (Point and Quadkey today; WKT/geohash/H3 are new classes.)
- **Reduction primitive** → an `IReductionStrategy` resolved by `ReductionCatalog`. (RawPassthrough,
  QuadtreeLod, Aggregate today; SignalM4 next.) The two current descents are deliberately *not* forced
  into one shared "pyramid" — a depth-first adaptive split and a breadth-first level pass are different
  algorithms; the genuinely shared mechanics are `DuckDbSession`, `TileSql`, and `ArrowTileWriter`.
- **Source adapter** → an `ISourceAdapter` resolved by `SourceAdapterCatalog`. (ClickHouse today.)
- **Reduction choice** is data-driven, not authored: [`BakePlanner`](../src/Colossus.Application/BakePlanner.cs)
  maps a source probe → `BakePlan` (reduction, depth, budget, root).

## Frontend (web/)

Config-driven: the client reads the manifest descriptor and one code path renders every mark/viewport.

- `lib/manifest.ts`, `lib/views.ts` — load the manifest + view registry API.
- `lib/schema.ts` — the canonical-schema mirror (above).
- `lib/tiling.ts` — pyramid math: `selectTiles`, `coverTiles`, `pointToTile`, `tileRect` (contract mirror).
- `lib/tileData.ts` — Arrow → typed arrays (points, polygons, bake-time triangles). Zero per-mark objects. Also bakes the per-mark GPU filter attribute (`filterValues`) once per tile from the view's filter slots. Under tile format 2 it decodes as views over the one retained buffer (no column copies, no triangle rebase); format 1 keeps the copy path.
- `lib/channels.ts` — channel helpers: the color channel, its observed domain (numeric range / categories), filter option discovery, and the canonical category order (`canonicalCategories`).
- `lib/gpuFilter.ts` — the GPU-filter mapping: filter slots per view, filter values per mark, and filter selections → `DataFilterExtension` `filterRange`/`filterEnabled` uniforms. A filter change touches no tile bytes.
- `lib/colors.ts` / `lib/schemes.ts` — color primitives (hex + interpolation) and the named scheme registry (sequential / diverging / categorical families).
- `lib/colorScale.ts` — the scale engine: `encoding.color` + observed domain → a `value → RGB` function, across all scale types and datatypes. Stays the CPU authority the GPU LUT is sampled from and tested against.
- `lib/colorLut.ts` — samples `colorScale.ts` into a small RGBA8 lookup-table texture + `domain`/`transform`/`kind` uniforms; parity with `colorScale` is asserted per scale type without a GPU.
- `lib/colorScaleExtension.ts` — a deck `LayerExtension` that uploads the LUT texture and maps the per-mark `getScaleValue` attribute → color in the vertex shader. Recoloring is a texture/uniform update; no per-mark data moves.
- `lib/deckData.ts` — memoized deck binary attributes (geometry built once; the per-mark `getScaleValue` value attribute is built once per (tile, channel); the GPU filter attribute rides in `data.attributes`; no CPU color array exists).
- `components/InspectPanel.tsx` — the pinned click-to-inspect readout, driven by the view's `inspect` config.
- `lib/tileCache.ts` — a framework-free `TileCache` publishing an immutable `TileSnapshot`; the
  `useTiles` hook consumes it via `useSyncExternalStore`, so React state stays derived, not juggled.
- `hooks/useViewData.ts`, `hooks/useTiles.ts`, `components/Hud.tsx`, `App.tsx` — thin composition.

## Testing

- `dotnet test` — pure bake logic (tiling, triangulation, planner, config validation, JSON, Arrow
  round-trips through in-memory DuckDB, the store), plus the cross-language tiling conformance.
- `web/`: `npm run test` (Vitest) — the tiling conformance mirror + cover/select algorithms.
- End-to-end: `dotnet run --project src/Colossus.Bake -- verify` asserts the fidelity invariant against
  baked tiles; the app renders through the preview MCP.
