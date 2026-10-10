# 2. Folder structure

The frontend layout was agreed and applied to `web/src` on 2026-10-10 (see [Frontend status](#status)). The repository top level and the backend are still proposals.

## Principles

1. **One home per concern.** Source, contracts, operations, docs, generated output and runtime data never share a folder.
2. **The frontend/backend boundary is a folder.** `frontend/` and `backend/` meet only through `contracts/`, and both test suites read it.
3. **Generated output and runtime data never live in the source tree.** All of it goes to ignored roots that are configured explicitly, not found by walking up to a marker file.
4. **Structure moves are their own commits** with no content changes, so `git log --follow` keeps working.

## Repository top level

Status: **decided** — the full split below. Root clean-up done on 2026-10-10 (stray root `bin/`/`obj/`, publish output, tool logs, empty `.github/`, merged worktrees and branches removed). The folder moves follow the preconditions below.

```
Colossus/
├─ README.md  CLAUDE.md  AGENTS.md
├─ .github/workflows/ci.yml
├─ .editorconfig  .gitattributes  .gitignore
│
├─ frontend/                     was web/
├─ backend/
│  ├─ Colossus.slnx  global.json  Directory.Build.props
│  ├─ src/                       Domain, Application, Infrastructure, Api, Worker, Cli
│  └─ tests/                     Unit, Integration, Docker
├─ contracts/                    the backend ↔ frontend interface
│  ├─ openapi/colossus.v1.json   committed snapshot; CI fails on drift
│  ├─ schemas/                   manifest + map definition JSON Schema
│  ├─ formats/                   tile, geometry codec, slab, render pack, fold response specs
│  └─ fixtures/                  was tests/fixtures, plus golden binaries written by C#
├─ ops/
│  ├─ docker/                    compose, trino, clickhouse
│  ├─ seed/                      seed scripts + legacy views/*.json
│  ├─ deploy/                    scripted publish + production config template
│  └─ showcase/
├─ tools/bench/                  CompanionBench + the web bench scripts (or delete them)
├─ docs/                         current docs; legacy/ holds everything written before 2026-10-10
│
├─ .data/        (ignored)       tiles/ staging/ media/ datasets/ — one configured root
└─ artifacts/    (ignored)       every bin/obj (UseArtifactsOutput), publish, dist, storybook-static
```

Why this shape:

- Anyone cloning the repo sees in seconds where the frontend lives (`frontend/`), what binds it to the backend (`contracts/`) and where the backend lives (`backend/`).
- `contracts/fixtures` replaces `tests/fixtures`, which the web tests reach today through `../../../../../tests/fixtures`. Both CI jobs read the same files, so drift fails a build instead of a demo.
- `artifacts/` and `.data/` remove the ~10 GB of build output and runtime data that currently sits next to source. `UseArtifactsOutput` in `Directory.Build.props` (supported since .NET 8) sends every project's `bin/obj` under `artifacts/`. Storybook and Vite get an explicit `outDir` there too.

A lighter alternative is to keep `src/` and `tests/` at the root (the .NET convention), rename `web/` to `frontend/`, and add `contracts/` and `ops/`. It means less churn but a less obvious boundary. The full split is recommended, because it makes the frontend/backend boundary visible at the top level.

### Preconditions — order matters

1. **Decouple runtime data from the repo root.** `RepoPaths` finds the root by walking up to `Colossus.slnx` (`src/Colossus.Infrastructure/RepoPaths.cs:11`). Moving the solution file into `backend/` without fixing this silently relocates `tiles/`, `staging/` and `views/`. Replace the marker walk with one explicit `Storage:Root` setting (environment variable in production, `.data/` in development), and make the five test-side `FindRepoRoot` copies use the same mechanism.
2. **Repoint fixture paths.** Five web tests and the C# fixture tests read `tests/fixtures`; point both at `contracts/fixtures`.
3. **Move `views/`.** It moves with `CatalogApiTests.cs:80` and `ProductCatalogTests.cs:161`, which read it.
4. **Then move folders,** one commit per top-level move, with no edits mixed in.

---

## Frontend: `frontend/src`

Until the repo split renames `web/` to `frontend/`, everything below applies to `web/src`.

### Naming

The frontend uses plain feature folders, the layout most React developers already know (the bulletproof-react style):
- each product area gets a folder under `features/`;
- code that more than one feature needs moves up into plainly named top-level folders.

Retired, and never used in new code:
- the Feature-Sliced vocabulary an earlier, unfinished migration started (`pages/`, `widgets/`, `entities/`, `shared/`, `model/` segments);
- the leftover `domain/` and `infrastructure/` folders.

They read like backend layers, and that migration was never finished. The backend keeps its .NET project names (`Domain`, `Application`, `Infrastructure`); this rule is about the frontend only.

### Rules

1. **A folder is named after the one job every file in it does.** If two files do different jobs, they belong in different folders.
2. **A file does one job, is named after what it exports, and stays under about 250 lines.**
3. **Features never import each other.** Anything two features need moves up into a shared folder: `api/`, `types/`, `lib/`, `components/`, `hooks/` or `utils/`. The screens in `app/routes/` are the only place features meet.
4. **Every feature uses the same folder names:** `components/`, `hooks/`, `forms/` and `utils/`. Special folders appear only where a feature needs them: `engine/`, `embed/`, `diagnostics/`.
5. **Each feature has one `index.ts`.** Screens import from it, never from a deep path. The engine has its own `index.ts`.
6. **The engine never imports React.**
7. **Tests sit next to the file they test,** with the same name.
8. **One path alias:** `@/` → `src/` (e.g. `@/lib/color/scale`). It replaces today's eight per-layer aliases, which are copied into four config files. `@types/…` is avoided because it collides with npm's `@types` packages.

### Top level

```
src/
├─ app/                  starts the app and defines the screens
│  ├─ main.tsx  App.tsx  router.tsx
│  └─ routes/            one component per screen; puts features together, holds no logic
│     ├─ managementRoutes.tsx, viewerRoutes.tsx   route modules (the viewer one is the lazy chunk)
│     ├─ maps/           MapCatalogPage, MapDetailPage, MapEditorPage
│     ├─ sources/        SourcesPage
│     ├─ viewer/         MapViewerPage (serves /maps/:id and /embed/:id; MapEmbedPage is planned, see Big files)
│     └─ RouteFeedback.tsx   loading, not found, error
│
├─ features/             one folder per product area
│  ├─ maps/              catalog, detail, editor, bake runs and releases
│  ├─ sources/           connections and datasets
│  └─ map-viewer/        the public viewer, the embed, the rendering engine
│
├─ api/                  every call to the backend, one file per resource
│  ├─ client.ts          one fetch wrapper, one error type, the base URL
│  ├─ queryClient.ts  queryKeys.ts
│  └─ maps.ts  bakes.ts  sources.ts  manifest.ts      requests + TanStack query options
├─ types/                the backend's data shapes; generated from OpenAPI later (04 A1)
│  └─ map.ts  bake.ts  source.ts  manifest.ts  viewConfig.ts  theme.ts
├─ components/           shared React components that know nothing about maps
│  ├─ ui/                Button, Card, Dialog, Field, Badge, Feedback, Page, ThemeToggle, RouteLink
│  └─ layouts/           ManagementLayout
├─ lib/                  the project's own non-React libraries used by more than one feature
│  ├─ color/             scale, legend, valueFormat, colorEncoding, schemes, palette
│  └─ perf/              the timing calls the engine makes: events, recorder, timing, netMeasure, resourceTiming
├─ hooks/                useTheme
├─ config/               paths.ts: every route URL in one place
├─ styles/               tokens, mixins, primitives, global styles
└─ utils/                classNames, format (dates, bytes)
```

### Inside each feature

```
features/maps/
├─ index.ts
├─ components/   MapCatalogTable, MapConfigurationBuilder, ColorEncodingEditor,
│                BakeRunHistory, BakeRunFilterBar, BakeRunTable
├─ hooks/        mapMutations.ts: save, archive, run, cancel, bake policy, pin and reconcile releases;
│                useMapRuns.ts: paged run history
├─ forms/        mapForm.ts: form values ↔ map definition, validation
└─ utils/        publicationStatus.ts, catalogState.ts, bakeRunStatus.ts

features/sources/
├─ index.ts
├─ components/   ConnectionForm, DatasetForm, ConnectionList, DatasetList, PreviewPanel
├─ hooks/        sourceMutations.ts
└─ forms/        sourceForms.ts

features/map-viewer/
├─ index.ts
├─ components/   ViewerControls, Legend, InspectPanel, EmbedDisclosure (MapCanvas is planned)
│  └─ layout/    ViewerShell, ViewerHeader, ViewerPanel, PanelSection, MapWindow,
│                EmbedShell, EmbedSidebar, EmbedStage — one file each
├─ hooks/        useViewData, useTiles, useMeasureFold
│                (planned: useViewerLayers, useViewerPicking, useViewerUrl, useEmbedBridge)
├─ embed/        embedUrl.ts, embedEvents.ts: the contract with sites that embed a map
├─ diagnostics/  PerfDashboard and its sections, stats, totals, frame probe; loaded only with ?perf=1
├─ utils/        viewerRoute.ts
└─ engine/       no React; one folder per pipeline stage (below)
```

### The engine

Today the engine is 22 files in one folder. The target is eight folders, one per pipeline stage, read in this order:

**view → tiles → network → decode → filters → fold → draw → workers**

A short `engine/README.md` listing the stages in that order is planned; it does not exist yet.

```
features/map-viewer/engine/
├─ index.ts                  the only way in
├─ view/       what each column in the manifest is for, and what each measure means
│  ├─ channelRoles.ts        measure, render and colourable channels; regime
│  ├─ measureGrammar.ts      parses  avg(speed) where tech = '5G'
│  └─ colorDomain.ts         colour channel, colour domain, canonical categories
├─ tiles/      which tiles the camera needs, and keeping them in memory
│  ├─ tileGrid.ts            tileKey, tileRect, pointToTile
│  ├─ tileSelection.ts       selectTiles, prefetchCandidates, coverTiles
│  ├─ camera.ts              initial view, viewport, visible bounds
│  └─ tileCache.ts           TileCache: byte budget, eviction, failure back-off
├─ network/    talking to the tile server
│  ├─ tileUrls.ts            tile, facts, pack-block and pack-dictionary URLs
│  ├─ serviceWorkerCache.ts  register, unregister, activate a tile version
│  ├─ arrowFetch.ts          fetch and parse Arrow
│  ├─ inflate.ts             zstd / gzip block inflation shared by both fetchers
│  ├─ packFetch.ts           pack blocks, slab planes, cell rows
│  └─ zstd.ts                zstd decompression (WebAssembly)
├─ decode/     raw bytes → typed arrays
│  ├─ tileData.ts            TileData and the column types
│  ├─ decodeTile.ts          picks the decoder for each tile format
│  ├─ columnSource.ts        one column-reading interface over a table or a set of blocks
│  ├─ points.ts              point positions
│  ├─ polygons.ts            polygon rings and the triangles made at bake time
│  ├─ geometryCodec.ts       compressed shapes → coordinates
│  ├─ columns.ts             Arrow column → Float32Array / dictionary / UTF-8 views
│  ├─ filterValues.ts        per-mark filter values read from Arrow
│  └─ tileSchema.ts          column names, grid size
├─ filters/    filter values, and whether each filter runs on the GPU or recomputes values
│  ├─ filterValue.ts         "(all)", "from..to", day numbers
│  ├─ filterRouting.ts       which filters go to the GPU and which recompute values
│  ├─ filterControls.ts      which control each column gets
│  ├─ discoverOptions.ts     discovers filter options from the root tile
│  └─ gpuFilterSlots.ts      GPU filter slot layout and ranges
├─ fold/       recompute measures when a filter changes them
│  ├─ foldContext.ts         active filters → fold input
│  ├─ aggregators.ts         the aggregator classes and finalizers
│  ├─ rowFold.ts             fold over row-form facts
│  ├─ companion.ts           decode the per-tile fact data
│  ├─ remoteFold.ts          ask the server to fold
│  ├─ foldCoordinator.ts     (later) replaces the async state machine inside useMeasureFold
│  └─ slab/                  the compressed per-tile fact cube
│     ├─ foldSlab.ts         entry: picks the sparse or dense fold per tile
│     ├─ slabDecode.ts       decode, assemble, merge
│     ├─ slabPlanes.ts       which planes a measure needs; fetch sizes
│     ├─ slabAxes.ts         axis and bin helpers shared by both folds
│     ├─ slabFolder.ts       the folder interface both folds implement
│     ├─ sparseFold.ts
│     └─ denseFold.ts
├─ draw/       hand the arrays to deck.gl and the GPU
│  ├─ tileDeckData.ts        tile → deck.gl binary attributes
│  ├─ colorLut.ts            colour scale → lookup texture
│  ├─ colorScaleExtension.ts the GLSL colour shader
│  └─ basemap.ts             MapLibre style per theme
└─ workers/    decoding on background threads
   ├─ tileLoader.ts          up to 4 workers, cancellation, main-thread fallback
   ├─ tile.worker.ts         the worker entry
   └─ jobs.ts                loadTile, loadPackedTile, loadColumns, loadCompanion
```

Where each of today's engine files goes:

| Today (`features/map-viewer/engine/`) | Goes to |
|---|---|
| `arrow.ts` | `network/arrowFetch.ts`, `network/inflate.ts`, `network/packFetch.ts` |
| `basemap.ts`, `colorLut.ts`, `colorScaleExtension.ts` | `draw/` |
| `channels.ts` (233) | `view/channelRoles.ts`, `view/colorDomain.ts`, `filters/filterRouting.ts`, `filters/filterControls.ts`, `filters/discoverOptions.ts` |
| `dates.ts` | `filters/filterValue.ts` |
| `deckData.ts` | `draw/tileDeckData.ts` |
| `embed.ts`, `embedMessages.ts` | `features/map-viewer/embed/embedUrl.ts`, `embedEvents.ts`; not engine code |
| `geometryCodec.ts` | `decode/geometryCodec.ts` |
| `gpuFilter.ts` | `filters/gpuFilterSlots.ts` |
| `measures.ts` (509) | `view/measureGrammar.ts`, `fold/foldContext.ts`, `aggregators.ts`, `rowFold.ts`, `companion.ts` |
| `remoteFold.ts` | `fold/remoteFold.ts` |
| `schema.ts` | `decode/tileSchema.ts` |
| `slab.ts` (709) | `fold/slab/{foldSlab,slabDecode,slabPlanes,slabAxes,slabFolder,sparseFold,denseFold}.ts`, `fold/aggregators.ts` |
| `tileCache.ts` | `tiles/tileCache.ts` |
| `tileData.ts` (885) | `decode/{tileData,decodeTile,columnSource,points,polygons,columns,filterValues}.ts`, `network/packFetch.ts`, `fold/companion.ts`, `workers/jobs.ts` |
| `tileLoader.ts` | `workers/tileLoader.ts` |
| `tileWorker.ts` | `workers/tile.worker.ts` |
| `tiling.ts` | `tiles/tileGrid.ts`, `tiles/tileSelection.ts` |
| `viewport.ts` | `tiles/camera.ts` |
| `zstdCodec.ts` | `network/zstd.ts` |
| `*.test.ts` | next to the file they test, split when the file splits |

**Dependency direction.** The stage folders form no import loop, and `architecture.test.ts` keeps it that way. The edges are:
- `network` → nothing;
- `view` → `network`;
- `filters` → `network`, `view`;
- `decode` → `filters`, `view`;
- `tiles` → `decode`;
- `draw` → `decode`;
- `fold` → `decode`, `filters`, `network`, `view`;
- `workers` → `decode`, `filters`, `fold`, `network`.

The measure grammar lives in `view/`, not `fold/`, because reading the manifest needs it and folding builds on it.

### Where today's code goes

| Today (`web/src`) | Goes to |
|---|---|
| `pages/maps/*.tsx` | `app/routes/maps/`; `MapViewerPage` → `app/routes/viewer/` |
| `pages/sources/SourceWorkspacePage.tsx` | `app/routes/sources/SourcesPage.tsx` |
| `pages/maps/maps.scss` (151 lines, shared by four pages) | one stylesheet per page |
| `main.tsx` | `app/main.tsx` (`index.html` points at it) |
| `app/routes/routeFeedback.tsx` | `app/routes/RouteFeedback.tsx`; the two route modules stay in `app/routes/` |
| `domain/map/manifest.ts` (261) | `types/manifest.ts` (a published release: tiles, packs, slabs, fold route) + `types/viewConfig.ts` (what to draw: channels, encoding, filters, measures) |
| `domain/map/colorScale.ts` (294) | `lib/color/scale.ts`, `legend.ts`, `valueFormat.ts` |
| `domain/map/colorEncoding.ts`, `schemes.ts`, `colors.ts` | `lib/color/colorEncoding.ts`, `schemes.ts`, `palette.ts` |
| `domain/map/view.ts` | deleted; nothing imports it |
| `infrastructure/api/mapApi.ts` | `loadManifest` → `api/manifest.ts`; tile URL builders → `engine/network/tileUrls.ts` |
| `infrastructure/cache/tileCacheServiceWorker.ts` | `engine/network/serviceWorkerCache.ts` |
| `entities/catalog/model/catalog.ts` (282) | `types/map.ts`, `types/bake.ts`, `types/source.ts`. Its `ChannelSpec` and `GeometrySpec` differ from the viewer's, so they are now `DatasetChannelSpec` and `DatasetGeometrySpec` in `types/source.ts` |
| `entities/catalog/api/catalogApi.ts`, `catalogQueries.ts` | `api/client.ts`, `api/queryKeys.ts` + `api/maps.ts`, `bakes.ts`, `sources.ts` |
| `shared/api/config.ts` | `api/client.ts` |
| `shared/query/queryClient.ts` | `api/queryClient.ts`; features import it, so it cannot live in `app/` |
| `shared/ui/*` | `components/ui/` (flat, because every file is a component); `classNames.ts` → `utils/` |
| `shared/ui/styles/*` | `styles/` |
| `shared/navigation/RouteLink.tsx` | `components/ui/RouteLink.tsx` |
| `shared/theme/useTheme.ts` | `hooks/useTheme.ts`; its `Theme` type → `types/theme.ts` |
| `shared/observability/perf.ts` (543) | recording side (record, timed, network timing, the event buffer) → `lib/perf/`; reading side (stats, totals, frame probe, GPU name, the `window.__perf` global) → `features/map-viewer/diagnostics/` |
| `widgets/management-shell/` | `components/layouts/ManagementLayout.tsx` |
| `widgets/map-catalog/MapCatalogTable.tsx` | `features/maps/components/` |
| `widgets/source-workspace/*` | `features/sources/components/` (`ConnectionList`, `DatasetList`, `PreviewPanel`) |
| `widgets/map-viewer/Controls.tsx` | `features/map-viewer/components/ViewerControls.tsx` |
| `widgets/map-viewer/Legend.tsx` (`LegendBox`) | `features/map-viewer/components/Legend.tsx` (`Legend`) |
| `widgets/map-viewer/InspectPanel.tsx`, `EmbedDisclosure.tsx` | `features/map-viewer/components/` |
| `widgets/map-viewer/ViewerShell.tsx` (eight components in one file) | `features/map-viewer/components/layout/`, one file each |
| `widgets/map-viewer/viewer-shell.scss` (431) | one stylesheet per component, same cascade order |
| `widgets/map-viewer/PerfDashboard.tsx` (304) | `features/map-viewer/diagnostics/`, split into section components |
| `features/map-management/*` | `features/maps/` (mutations → `hooks/`, `mapForm` → `forms/`, editors → `components/`) |
| `features/map-management/model/presentation.ts` (four unrelated jobs) | `config/paths.ts` (viewer and embed links), `utils/format.ts` (dates, bytes), `features/maps/utils/publicationStatus.ts`, `features/maps/utils/catalogState.ts` |
| `features/source-management/*` | `features/sources/` (mutations → `hooks/`, `sourceForms` → `forms/`, forms → `components/`) |
| `features/map-viewer/model/viewRoute.ts` | `features/map-viewer/utils/viewerRoute.ts` |
| `features/map-viewer/model/viewerDiagnostics.ts` | `features/map-viewer/diagnostics/reloadViewerCold.ts` |
| `engine/colorScale.test.ts`, `engine/colors.test.ts` (they test `domain/` code) | next to `lib/color/` |
| `public/embed.html`, `public/__perf-harness.js` | `tools/` or delete (stop shipping them) |
| `scripts/bench-*.ts` | `tools/bench/` or delete (both are broken today) |

### Boundary checks

`architecture.test.ts` is rewritten. Today it enforces the old layer order and even forbids top-level `components/`, `hooks/` and `lib/` folders. The new version checks:

1. No feature imports another feature.
2. Shared folders (`api`, `types`, `components`, `hooks`, `lib`, `config`, `styles`, `utils`) never import `features/` or `app/`.
3. Nothing outside a feature imports past its `index.ts`.
4. `engine/` imports no React, router or components.
5. The scan covers dynamic `import()` and `.storybook/`; today's misses both.
6. The retired folders (`domain`, `infrastructure`, `entities`, `widgets`, `pages`, `shared`) stay gone.

### Big files and how they split

| File (lines) | Split into |
|---|---|
| `tileData.ts` (885) | `decode/{tileData,decodeTile,points,polygons,columns,filterValues}`, `network/packFetch`, `fold/companion`, `workers/jobs` |
| `slab.ts` (709) | `fold/slab/{slabDecode,slabPlanes,sparseFold,denseFold}` + `fold/aggregators` |
| `perf.ts` (543) | `lib/perf/` + `features/map-viewer/diagnostics/` |
| `MapViewerPage.tsx` (533) | `MapViewerPage` and `MapEmbedPage` (about 100 lines each; the 15 `isEmbed` branches become two pages) + `useViewerLayers`, `useViewerPicking`, `useViewerUrl`, `useEmbedBridge` + `components/MapCanvas` |
| `measures.ts` (509) | `view/measureGrammar` + `fold/{foldContext,aggregators,rowFold,companion}` |
| `viewer-shell.scss` (431) | one stylesheet per component |
| `useMeasureFold.ts` (361) | `fold/foldCoordinator` (a plain class) + a thin hook |
| `PerfDashboard.tsx` (304) | section components in `diagnostics/` |
| `colorScale.ts` (294) | `lib/color/{scale,legend,valueFormat}` |
| `catalog.ts` (282) | `types/{map,bake,source}` |
| `manifest.ts` (261) | `types/{manifest,viewConfig}` |
| `channels.ts` (233) | `view/{channelRoles,colorDomain}` + `filters/{filterRouting,filterControls}` |
| `ViewerShell.tsx` (130, eight components) | one file per component in `components/layout/` |
| `presentation.ts` (four unrelated jobs) | `config/paths`, `utils/format`, `features/maps/utils/{publicationStatus,catalogState}` |
| `maps.scss` (151, four pages) | one stylesheet per page |

### Where to look

| Question | File |
|---|---|
| Which tiles load when I zoom? | `features/map-viewer/engine/tiles/tileSelection.ts` |
| Why is this polygon red? | `lib/color/scale.ts`, then `engine/draw/colorScaleExtension.ts` |
| Does this filter run on the GPU or recompute values? | `engine/filters/filterRouting.ts` |
| How is a measure like `avg(speed) where tech = '5G'` parsed? | `engine/view/measureGrammar.ts` |
| What does an embed send to its host page? | `features/map-viewer/embed/embedEvents.ts` |
| Where are the backend calls? | `api/` |
| What does a manifest contain? | `types/manifest.ts` |

### Status

**Done on 2026-10-10.** Nothing outside `web/` changed.

The restructure ran in four steps: the `@/` alias and whole-file moves, the splits outside the engine, the engine stage folders with the feature `index.ts` files, and the new boundary checks. It was then reviewed against a build of the original code.

| Check | Before | After |
|---|---|---|
| Type check, lint | clean | clean |
| Unit tests | 24 files, 223 tests | 40 files, 229 tests; the same 221 test cases outside `architecture.test.ts`, which grew from 2 cases to 8 |
| Build and bundle budget | 373.2 KiB initial JS | 368.4 KiB; the viewer is still lazy, and the tile worker still has no deck.gl or React |
| Storybook | 4 failed, 4 passed | 8 passed: the viewer stories, which passed no `controls` prop, are fixed and renamed "Map Viewer" |
| Visual tests (Playwright) | viewer story timed out | 4 passed; the committed screenshots match unchanged |
| Compiled CSS | — | the same declarations in every chunk; a few rules reordered, none visibly |

How the review checked "no behaviour change": 699 of 727 top-level declarations are identical. The other 28 differ only in import paths, fixture paths, the renames listed above, or perf code that now reads the same live state through accessor functions.

Worth knowing:
- `web/package.json` now marks only stylesheets as side-effectful (`"sideEffects"`). Without it, the feature `index.ts` files pulled the whole viewer into the first page load. The review found no module whose load-time code depends on running for its side effects.
- `architecture.test.ts` enforces rules 1–6 of [Boundary checks](#boundary-checks), a ban on `model/` folders, no engine imports of hooks, and no import loop between engine stage folders. Each rule was shown to fail on a deliberate violation. Not enforced: test placement and file length.

Follow-ups from the review, also done:
1. Moved `measureGrammar.ts` into `view/`, which removed the folder-level import loop.
2. Moved the `Theme` type into `types/theme.ts`, so the engine no longer needs `hooks/`.
3. Fixed the viewer Storybook stories and renamed them from "Widgets" to "Map Viewer".
4. Renamed `workerPool.ts` → `tileLoader.ts`, `deckAttributes.ts` → `tileDeckData.ts`, `filterOptions.ts` → `discoverOptions.ts`, `viewerDiagnostics.ts` → `reloadViewerCold.ts`, to match their exports.

Still over 250 lines:
- `MapViewerPage.tsx` (505) and `useMeasureFold.ts` (355) are next, under [04](04-long-term.md) C1.
- `ColorEncodingEditor.tsx` (292) and `SourcesPage.tsx` (259) are due for a rewrite in the Map Studio work (B1).
- Two test files: `decodeTile.test.ts` (379) and `foldSlab.test.ts` (290).

Left for the next pass, because they change logic rather than move it:
- splitting `MapViewerPage` into two pages and four hooks;
- `FoldCoordinator`;
- one aggregator implementation.

Renaming `web/` to `frontend/` belongs to the repo-level move.

---

## Backend: `backend/`

This is lower priority than the frontend, so it is shown for completeness.

```
backend/src/
├─ Colossus.Domain/          Catalog/ Maps/ (ViewConfig → MapSnapshot, one type per file) Manifest/ Measures/ Tiling/
├─ Colossus.Application/     Catalog/ Baking/ (one class per stage, RunStateMachine, RetryPolicy) Sources/ Fold/ (new port)
├─ Colossus.Infrastructure/  Persistence/ (versioned migrations, one repository per aggregate, one lock class)
│                            Sources/{Common,ClickHouse,Trino,Parquet}  Baking/{Reduction,Tiles,Stores}  Fold/  Options/
├─ Colossus.Api/             was Server: controllers, static tiles, auth, CORS policy
├─ Colossus.Worker/          Hangfire server + schedulers in their own process
└─ Colossus.Cli/             was Bake; goes through the run/release model
backend/tests/
├─ Colossus.Tests.Unit/          pure: domain, application, formats, codecs
├─ Colossus.Tests.Integration/   DuckDB + filesystem, no Docker
└─ Colossus.Tests.Docker/        MySQL, ClickHouse, Trino; [Trait("Category","Docker")]
```

---

## Docs: `docs/`

```
docs/
├─ README.md            index
├─ architecture/        system overview, backend, frontend, decisions/ (ADRs)
├─ operations/          local development, deploy, runbooks
├─ review/              this review
└─ legacy/              everything written before 2026-10-10
```

The format specs live in `contracts/formats/`, next to their fixtures, not in `docs/`. `CLAUDE.md` and `AGENTS.md` at the root stay short: commands, boundaries and rules, with links into `docs/`.
