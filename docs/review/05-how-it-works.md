# 5. How Colossus works — from the data source to the pixels

Code-verified on 2026-10-10 against `main` at `34ee146`. Paths are relative to the repo root. Statements marked (unverified) were read from code but never executed. Frontend paths follow the folder restructure of 2026-10-10 ([02](02-folder-structure.md)); the engine lives in `web/src/features/map-viewer/engine/`, split into eight stage folders.

## The mental model

Colossus draws very large datasets (10M–1B+ rows) on a map without sampling or simplifying the marks. The trick is to do the heavy work **once, ahead of time**, and send the browser data it can upload to the GPU as-is.

```
source (ClickHouse | Trino | Parquet)
   │  bake: probe → extract to staging Parquet → plan → reduce into a tile pyramid → pack → verify → publish
   ▼
immutable release on disk or S3:  tiles/<slug>/<version>/  +  tiles/<slug>/latest.json
   │  served as static files (long cache, Range, brotli)
   ▼
browser: manifest → choose tiles for the viewport → fetch + decode in workers (zero-copy Arrow)
   → deck.gl binary attributes → GPU filter slots + colour lookup texture → pixels
```

There are two independent frontends in one SPA:

- **Management app** (`/maps`, `/sources`, `/maps/new`, `/maps/manage/:id[/edit]`) — talks to the JSON API.
- **Viewer** (`/maps/:slug`, `/embed/:slug`) — talks **only** to static files, plus one optional fold endpoint.

## Glossary

| Term | Meaning |
|---|---|
| Connection | Credentials and endpoint for a source engine (ClickHouse, Trino, Parquet provider) |
| Dataset | A query on a connection plus a geometry mapping plus typed channels |
| Channel | A column the map can use. **Role**: `measure`, `dimension`, `temporal` or `identity`. **Type**: `f32`, `f64`, `u8`, `u16`, `i32`, `i64`, `dict` (category) or `date` |
| Map | What to draw from a dataset: viewport, mark, bake filters, interactive filters, measures, colour encoding, inspect fields. Called a `MapDraft` in the API and a `ViewConfig` inside the bake ("view" is the legacy name) |
| Slug | The map's public id, used in viewer URLs and as the tile folder name. The API uses GUIDs |
| Bake | Turning a map plus its dataset into a release of tiles |
| Release / version | One immutable bake output, `v<UTC yyyyMMddTHHmmssfffZ>-<8 hex>-a<attempt>`. `latest.json` points at the current one |
| Manifest | `manifest.json` in a release: the tile index, channel domains, format flags, fold routing, and the view config |
| Tile / LOD | Quadtree tiles `z/x/y`. Internal tiles hold merged marks; leaf tiles hold every mark |
| Render pack | Optional single `render.pack` file holding every tile's columns as zstd blocks, fetched with HTTP Range |
| Companion / slab | `facts.pack`: per-tile partial aggregates over (mark × category × time), so measures can be recomputed when a filter changes |
| Measure | A computed per-mark value from a closed grammar, e.g. `sum(tests)`, `wavg(download_mbps, tests)`, `argmax(operator, sum(tests))` |
| Regime | **Row regime**: marks are source rows. **Group regime**: a map has measures, so marks are aggregated per geometry, and filters that change the aggregation trigger a fold |
| perMark / perFact filter | perMark: filtered on the GPU. perFact: changes the measure, so it needs a fold |
| Fold | Recomputing measures for the current filter context from companion partials — in the browser, or on the server via DuckDB |

## Lifecycle, step by step

1. **Connection.**
   - UI: `web/src/app/routes/sources/SourcesPage.tsx`; form fields come from `GET /api/source-adapters`.
   - Server: `ConnectionsController` → `ProductCatalogService.CreateConnectionAsync` → MySQL `connections` table.
   - Secrets are write-only from the API's point of view, but stored in plain text.
2. **Discovery and preview.**
   - Discovery: `POST /api/connections/{id}/test`, `GET …/locations`, `POST …/schema` (`src/Colossus.Server/Controllers/SourceAdaptersController.cs`).
   - Channel role and type suggestions are frontend heuristics (`web/src/features/sources/forms/sourceForms.ts:49-112`).
   - Bounded preview: `POST /api/datasets/{id}/preview` (50 rows, 10 s ceiling).
3. **Dataset.** `POST /api/datasets` → `DatasetDraft.Validate` (`src/Colossus.Domain/Catalog/CatalogModels.cs:86`).
4. **Map.**
   - `web/src/app/routes/maps/MapEditorPage.tsx` → `draftFromForm` (`web/src/features/maps/forms/mapForm.ts:76`) → `POST` or `PUT /api/maps` (PUT carries `expectedRevision`).
   - Server: `MapDraft.ToSnapshot` → `ViewConfig.Validate` (`src/Colossus.Domain/Model/ViewConfig.cs:51-114`). This is the real validation; the frontend checks only slugs and hex colours.
5. **Save and bake.**
   - The UI makes three separate calls: save the map, `POST /api/maps/{id}/runs`, then `PUT /api/maps/{id}/bake-policy`. A partial failure surfaces as `ManagedMapPartialSaveError` (`web/src/features/maps/hooks/mapMutations.ts:85`).
   - The run snapshots the view config. Hangfire on MySQL runs it in the **API process**, on queue `bakes` (`src/Colossus.Server/Scheduling/*`).
   - Recurring schedules come from bake policies.
   - The UI polls `GET /api/maps/{id}` every 1.5 s while a run is active.
6. **Bake** (`src/Colossus.Application/Catalog/BakeRunExecutor.cs` → `src/Colossus.Application/BakeViewUseCase.cs`):
   1. Probe the source.
   2. Extract to staging Parquet.
   3. `BakePlanner` picks the reduction, zoom range and point budget.
   4. Choose the tile format: 3 for polygon, rect and heat marks; 2 otherwise.
   5. Reduce into tiles (DuckDB, out-of-core).
   6. Optionally write the render pack (`Rendering:PackTiles`, off by default).
   7. Write the companion slab for group-regime maps.
   8. Price the fold route (client or remote).
   9. Write the manifest.
   10. Verify row counts.
   11. Publish.
7. **Publish and retention.**
   - The release is recorded as Prepared, `latest.json` is flipped, then it is marked Published.
   - `ReleaseMaintenanceService` keeps the current, pinned, newest-N and recent releases and deletes the rest.
8. **Viewer.** URL → manifest → tiles → decode → deck.gl layers. Details below.
9. **Interaction.**
   - perMark filters change GPU uniforms only.
   - perFact filters trigger a fold.
   - Clicking a mark triggers inspect: lazy column fetch, then a fold for that mark.

## What the browser receives

### JSON API (management app only)

Bases: `/api/maps`, `/api/connections`, `/api/datasets`, `/api/source-adapters`, `/api/views`.

| Call | Purpose |
|---|---|
| `GET/POST /api/connections`, `POST …/{id}/test`, `GET …/{id}/locations`, `POST …/{id}/schema` | Connections and discovery |
| `GET /api/source-adapters` | Adapter descriptors that drive the connection form |
| `GET/POST /api/datasets`, `POST …/{id}/preview` | Datasets and bounded preview |
| `GET /api/maps?includeArchived`, `GET /api/maps/{guid}` | List and detail (detail includes the draft, dataset, policy, runs and releases) |
| `POST /api/maps`, `PUT /api/maps/{guid}?expectedRevision`, `DELETE …` (archive) | Map definition, with optimistic concurrency (409 on a stale revision) |
| `POST /api/maps/{id}/runs`, `POST …/runs/{runId}/cancel` | Bake now and cancel |
| `PUT /api/maps/{id}/bake-policy?expectedRevision` | Schedule and retention |
| `PUT …/releases/{rid}/pin?pinned`, `POST …/releases/reconcile` | Release pinning and retention |
| `POST /api/views/{slug}/fold` | **Viewer**, remote fold. Returns an Arrow stream |

- **Conventions:** camelCase JSON; enums as camelCase strings; nulls omitted; `TimeSpan` as `"d.hh:mm:ss"`.
- **Errors:**
  - 400: `ValidationProblemDetails` with `errors{field:[msg]}`.
  - 404 and 409: `{error}`.
  - Source failures: 408, 502 or 503 with `sourceErrorKind` and `transient`.
- **Defects to know about:**
  - There is no auth.
  - Unknown `/api/*` routes return the SPA's HTML with status 200.
  - Swagger is at `/swagger`, the OpenAPI document at `/openapi/v1.json`. Neither is used to generate the TypeScript types.

### Static release files (viewer)

Served from `Storage:TilesDirectory` under `/tiles` (`src/Colossus.Server/Program.cs:90-114`), or from S3/R2 in production.

| URL | Cache |
|---|---|
| `{TILES_BASE}/{slug}/latest.json` → `{"version":"v…"}` | 60 s |
| `{TILES_BASE}/{slug}/{version}/manifest.json` | immutable |
| `…/{version}/{z}/{x}/{y}.arrow` (brotli sibling `.arrow.br` negotiated) | immutable |
| `…/{version}/render.pack?tile=z/x/y&r=off-len` + `Range` header; `render.dict` | immutable |
| `…/{version}/facts.pack` + `facts.dict` (companion slab, ranged) | immutable |

Old versions stay readable until retention deletes them.

The service worker (`web/public/sw.js`) caches versioned tiles and ranged pack blocks, cache-first, in one cache per version. It **does not** cache `latest.json`, manifests, dictionaries or API calls. Add `?nosw=1` to unregister it, e.g. for cold measurements.

## Formats, in one paragraph each

Specs to salvage live under `docs/legacy/archive/2026/2026-08-10-pre-index/` (`companion-scale/SLAB-FORMAT.md`, `render-pack/PACK-FORMAT.md`, `VIEW_CONFIG.md`).

- **Manifest** (`src/Colossus.Domain/Model/Manifest.cs` ↔ `web/src/types/manifest.ts` and `web/src/types/viewConfig.ts`).
  - Fields: `tileFormat`, `version`, `view`, `reduction`, `regime`, `root`, `minZoom`, `maxZoom`, `tiles[{z,x,y,count,isLeaf}]`, `channelDomains`, plus the optional `renderPack`, `companionSlab`, `companionPack`, `factChannels`, `foldRoute`, `factsParquet`.
  - `channelDomains` gives category channels their values in canonical order, capped at 4096, and gives numeric channels min/max/quantiles.
  - The client gates behaviour on `tileFormat` and on which fields are present. `schemaVersion` is written but never read.
- **Arrow tile** (`src/Colossus.Infrastructure/Tiles/ArrowTileWriter.cs`, `ArrowColumnBuilder.cs` ↔ `web/src/features/map-viewer/engine/network/arrowFetch.ts` and `web/src/features/map-viewer/engine/decode/`).
  - One record batch with no nulls.
  - Columns:
    - Points: `x`, `y` as f32, plus `merged_count` on internal tiles.
    - Measures: always f32, with NaN for null.
    - Categories: `Dictionary<Int8|16|32, Utf8>`, pre-seeded so a code equals its index in `channelDomains`.
    - Identity: plain Utf8.
    - Dates: Date32.
  - Polygons in format 2 use `geometry`, `part_offsets` and `triangles` lists (triangulated at bake). Format 3 puts one binary `geom3` payload in row 0.
  - Column names are shared through `src/Colossus.Domain/Tiling/TileSchema.cs` ↔ `web/src/features/map-viewer/engine/decode/tileSchema.ts`.
- **Geometry codec** (`GeometryCodec.cs` ↔ `decode/geometryCodec.ts`), little-endian.
  - **Rect** codec: templates plus corner tables.
  - **Delta** codec: f32 bits → delta → zigzag → byte planes.
  - Per-row triangle counts are not stored. Both sides re-derive them from the triangulation rule, so the two triangulators must stay identical (pinned by `tests/fixtures/geometry-codec-cases.json`).
- **Render pack** (`RenderPackWriter.cs` ↔ `network/packFetch.ts:147-224`, `workers/jobs.ts:28`).
  - Each tile's columns are standalone zstd-compressed Arrow blocks, ordered `@geom` → first-paint channels → the rest, so first paint is one Range request.
  - The first-paint set must match the client's GPU slot rule, or the first filter blanks the view.
- **Slab companion** (`SlabCompanionWriter.cs`, `SlabPlan.cs` ↔ `fold/slab/`).
  - zstd level 19 with a trained shared dictionary.
  - Per leaf tile it is sparse (per-mark cell ids + partial planes) or dense (cell-row blocks of raw f32), chosen by occupancy ≥ 0.5.
  - Plane names: `cnt`, `sum__<ch>`, `swp__<ch>__<w>`, `min__<ch>`, `max__<ch>`.
- **Fold response** (`src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs` ↔ `fold/remoteFold.ts`).
  - An Arrow stream with one column per measure, rows in mark order.
  - Schema metadata `tiles` is `[[tileKey,count],…]`.

Cross-language fixtures in `tests/fixtures/*.json` (tiling, measures, geometry codec, slab) are read by both the xUnit and the Vitest suites. Nothing pins manifest, tile, pack or fold bytes yet ([03](03-quick-wins.md) item 4.4).

## The frontend at runtime

### Management app

- React Router 7 route modules with loaders that prefetch TanStack Query data (`web/src/app/router.tsx`).
- The API client, one request file per resource and the query keys live in `web/src/api/`; the response types live in `web/src/types/`.
- Mutation hooks live in `web/src/features/*/hooks/`.
- Form models are pure, tested functions in `web/src/features/*/forms/`.

### Viewer pipeline (URL → pixels)

1. **Route.** `/maps/:slug` (lazy chunk) → `MapViewerPage` (`web/src/app/routes/viewer/MapViewerPage.tsx`). Embed parameters are read once from `window.location` (`:50`).
2. **Manifest.** `useViewData` → `loadManifest` reads `latest.json` then `manifest.json` (`web/src/api/manifest.ts:5`). Then `activateTileVersion` tells the service worker to drop other versions, and `discoverOptions` builds the filter options.
3. **Colour.** `describeColorDomain` → `buildColorLut` (lookup texture) and `describeLegend`. All three come from the same scale resolver, so the legend and the GPU agree.
4. **Camera and slots.** `initialViewState` (`tiles/camera.ts`) sets the camera. `filterSlots` (`filters/gpuFilterSlots.ts:38`) assigns up to 4 GPU filter slots.
5. **Tile selection.** On every camera change, `useTiles` → `selectTiles` (`tiles/tileSelection.ts`) → `TileCache.ensure` → `tileLoader.load`.
6. **Worker decode.** `workers/tile.worker.ts`, fed by the pool in `workers/tileLoader.ts`:
   - fetch, using Range for pack blocks;
   - inflate with zstd or gzip;
   - decode into `TileData` with filter values;
   - transfer back zero-copy.
7. **Cover and prefetch.** `TileCache` publishes immutable snapshots (`useSyncExternalStore`). `coverTiles` picks what to draw. `ensureColumns` lazily fetches extra columns from the pack.
8. **Fold**, only when a perFact filter is active (`web/src/features/map-viewer/hooks/useMeasureFold.ts`):
   - companion planes are fetched in workers;
   - the fold itself runs on the **main thread**;
   - or a remote fold is requested (below).
9. **Layers.** `tileDeckData` (`draw/tileDeckData.ts`) builds deck.gl binary attributes. Polygons become `SolidPolygonLayer`; everything else becomes `ScatterplotLayer`. Both use `DataFilterExtension` plus the colour-scale shader extension.
10. **Canvas.** Geographic maps use MapLibre (CARTO basemap by default) with a deck overlay; orthographic views use `DeckGL` directly.
11. **Events.**
    - `onMove` updates the camera, which re-enters step 5.
    - `onMoveEnd` rewrites the URL.
    - `onPick` → `ensureColumns` + `foldInspect` → inspect panel.
    - In embed mode, `postEmbedEvent` sends `ready`, `error`, `camera` and `selection`.

### Fold: browser or server?

- **No fold** unless the map is group-regime and a perFact filter is active. The baked default values render as-is.
- **Route.** `?fold=remote|client` overrides. Otherwise `manifest.foldRoute.execution` decides, chosen at bake time by comparing the worst tile's companion bytes to `FoldRouting:BudgetBytes` (32 MB).
- **Remote fold:**
  - One `POST /api/views/{slug}/fold` per viewport, carrying `{version, measures (names), context, tiles}`.
  - The server folds from the release's `facts.parquet` with DuckDB and needs the release on its **local disk** (unverified for S3 deployments).
  - On failure it retries every 1.5 s, with no fallback to a local fold.
- **Measure grammar:**
  ```
  agg := sum|avg|min|max(ch) | count() | wavg(value, weight)
  expr := agg [where dim = 'v'] | share(agg) where dim = 'v' | argmax|argmin(dim, agg)
  ```
  It is parsed identically in `src/Colossus.Domain/Measures/MeasureParser.cs` and `web/src/features/map-viewer/engine/view/measureGrammar.ts:38`, and pinned by `tests/fixtures/measure-cases.json`. argmax/argmin produce a category.

### Embed contract

- **Route:** `/embed/:slug`.
- **URL parameters:** `color`, `scale`, `bins`, `midpoint`, `scheme`, `reverse=1`, `theme`, `lng`/`lat`/`z`, `f_<channel>=…` (`web/src/features/map-viewer/embed/embedUrl.ts`).
- **Outbound messages** (`embed/embedEvents.ts`): `{source:'colossus.map-viewer', version:1, type, mapId}`.
  - `type` is one of `ready{release}`, `error{message}`, `camera{longitude,latitude,zoom}` (debounced 160 ms), or `selection{title?,rows}|null`.
  - Messages are posted to target origin `'*'`. There is no inbound API.
- `web/public/embed.html` is a demo parent page, not part of the product.

## Configuration

| Where | Knobs |
|---|---|
| Frontend (build-time) | `VITE_TILES_BASE` (default `http://localhost:5174/tiles`); `VITE_API_BASE` (otherwise derived by regex from the tiles base — set it explicitly); `VITE_BASEMAP_STYLE`, `VITE_BASEMAP_STYLE_LIGHT` |
| Frontend (runtime URL flags) | `?perf=1` (dashboard), `?nosw=1` (no service worker), `?fold=remote\|client`, embed parameters above |
| Server `appsettings.json` sections | `ConnectionStrings`, `Database`, `Scheduling`, `BakeExecution`, `BakeResources`, `Storage`, `ReleaseArtifacts` (file or S3), `ReleaseMaintenance`, `ClickHouse`, `ParquetSources`, `Trino`, `FoldRouting`, `Rendering`, `Server` |
| Paths | Relative `Storage` paths resolve against the folder containing `Colossus.slnx` (`src/Colossus.Infrastructure/RepoPaths.cs`); absolute paths bypass that |

## Running it locally (as it is today)

1. `docker compose -f docker/docker-compose.yml up -d` starts MySQL 3306, ClickHouse 8123/9000, Trino 8080, and a Postgres catalog for Trino.
2. `dotnet run --project src/Colossus.Server` starts the API, the Hangfire worker, `/tiles` and `/swagger` on **5174**. Startup immediately dispatches any due scheduled bakes.
3. `npm --prefix web run dev` starts Vite on 5173 (5212 through `.claude/launch.json`). There is no proxy; the browser calls 5174 directly, which works only because CORS is wide open.
4. Seed data by hand: `scripts/seed-mobile-coverage.ps1` and `docker/seed-geonames.sh`.
5. Tests:
   - `npm --prefix web test` runs the frontend suite and needs `tests/fixtures` from outside `web/`.
   - `dotnet test` needs MySQL, ClickHouse and Trino running, and fails rather than skips without them.

## Ten things a frontend developer must know

1. **`MapViewerPage.tsx` is the viewer application** (`web/src/app/routes/viewer/`, 505 lines). Tiles, fold, filters, colour and embed all meet there. Splitting it into two pages and four hooks is the next pass ([04](04-long-term.md) C1).
2. **The engine is a library.** It lives in `web/src/features/map-viewer/engine`, has no React, and is split into eight folders in pipeline order: `view`, `tiles`, `network`, `decode`, `filters`, `fold`, `draw`, `workers`. Everything outside it goes through `engine/index.ts`. Changing the tile columns, geometry codec, measure grammar or tiling maths means changing C# as well, and the shared fixtures.
3. **The viewer never calls the management API.** It reads static release files, plus one optional fold POST.
4. **The manifest is the real contract,** and the client gates on `tileFormat` and on which fields are present. Both packed and per-file tile layouts exist in the wild.
5. **Filter state is strings.** `(all)` means no filter, `a..b` is a range, and the URL form is `f_<name>=`. Sentinel constants are duplicated in three files.
6. **Folding runs on the main thread.** Only fetch and decode run in workers; remote fold is the escape hatch for heavy maps.
7. **The service worker is registered in development too.** Use `?nosw=1` when measuring cold loads.
8. **URL state is write-only.** Back and forward do not restore the viewer.
9. **The management API is unauthenticated, and its JSON types are hand-mirrored.** `filters`, `measures`, `encoding` and `inspect` are `unknown` in TypeScript. A domain change in C# is an API change.
10. **Build settings.** `npm run build` enforces the bundle budget: initial JS ≤ 450 KiB, viewer lazy-loaded. Set `VITE_API_BASE` and `VITE_TILES_BASE` for any non-local build.
