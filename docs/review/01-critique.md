# 1. Critique

File paths cite the code as reviewed on 2026-10-10, before the frontend folder restructure. [02](02-folder-structure.md#where-todays-code-goes) maps each old path to its new home.

## Verdict

The engine is the valuable part, and most of its complexity is inherent and well handled:

- Arrow tiles decoded zero-copy and handed to deck.gl as binary attributes.
- GPU filter slots and a colour lookup texture, so recolouring and filtering never touch per-mark data.
- Triangulation done at bake time, not in the browser.
- An immutable release model: prepare → verify → flip a pointer.
- Cross-language fixtures that pin the C# writer and the TypeScript reader to the same bytes.

Around that engine sits a product layer built in about twelve commits between 2026-08-10 and 2026-08-21: map management, sources, scheduling, the MySQL catalog. Almost all the introduced complexity lives there, plus in the repo and tooling.

The map configuration screen is the weakest part of the product, and the cause is structural, not cosmetic — see §1.

Size, for orientation:

| Area | Lines |
|---|---|
| Backend (Domain / Application / Infrastructure / Server) | 1.5k / 2.5k / 9.4k / 0.9k |
| Backend tests | 7.8k (205 tests) |
| Frontend TS/TSX incl. tests | 14k, of which the rendering engine is ~4.3k source + 2.3k tests |
| History | 92 commits on `main`, 2026-07-07 → 2026-08-21, no CI |

---

## 1. Map configuration and management UI

The screens were captured from the running app: `/maps`, `/maps/manage/:id`, `/maps/manage/:id/edit`, `/sources`, `/maps/:slug`.

### 1.1 The root cause: a JSON editor wearing form controls — Introduced, High

- `MapFormState` stores `bakeFilters`, `filters`, `measures`, `encoding` and `inspect` as **JSON strings** (`web/src/features/map-management/model/mapForm.ts:10-14`).
- The builder re-parses all five strings on every render and `JSON.stringify`s back on every change (`web/src/features/map-management/ui/MapConfigurationBuilder.tsx:45-62`).
- "Automatic filters" is encoded as "the string is empty" (`MapConfigurationBuilder.tsx:44`).
- An "Advanced configuration JSON" section edits the same strings directly (`:166-176`). Broken JSON there silently turns the guided UI empty, and the next guided edit overwrites what you typed (`:194-210`).

Everything below follows from this. The form has no typed model, so it cannot offer typed controls.

### 1.2 Typing where the system already knows the answer

| What you type | Where | What the system already knows | Label |
|---|---|---|---|
| Bake filter as raw SQL, one per line | `MapConfigurationBuilder.tsx:100-109` | Column names, roles and types of the dataset. Each line is pasted into `WHERE (…)` (`src/Colossus.Infrastructure/ClickHouse/ClickHouseAdapter.cs:384`) | Need: Inherent. SQL-only UI: Introduced |
| Measure expression, e.g. `argmax(operator, wavg(download_mbps, tests))` | `MapConfigurationBuilder.tsx:143` | The grammar is closed (9 verbs) and already parsed client-side (`web/src/features/map-viewer/engine/measures.ts:42`) | Grammar: Inherent. Typing it: Introduced |
| Measure name | `:142` | Derivable (`sum_tests`). Renaming orphans references in encoding and inspect | Introduced |
| Filter default as `min..max` text | `:124` | The control kind. Range and date components already exist in the viewer (`web/src/widgets/map-viewer/Controls.tsx`) | Introduced |
| Filter "control" dropdown with exactly one option | `:121-123` | `multiSelect` is supported server-side (`ViewConfig.cs`) but never offered | Introduced |
| Category colour override, keyed by typed name | `ColorEncodingEditor.tsx:231` | Category values are in the manifest after a bake (`channelDomains`) | Introduced |
| Category order as newline-separated text | `ColorEncodingEditor.tsx:213` | Same | Introduced |
| Schedule as four boxes (days / hours / minutes / seconds); time zone hard-coded UTC | `web/src/pages/maps/MapEditorPage.tsx:119,219-222` | A handful of sensible cadences | Introduced |
| Mark: 7 options offered | `MapEditorPage.tsx:24,205` | The viewer renders only polygons and points (`MapViewerPage.tsx:225,338,356`) | Introduced |
| Dataset query typed by hand | `web/src/features/source-management/ui/DatasetForm.tsx:73` | "Discover locations" lists the tables, but as dead text (`web/src/widgets/source-workspace/SourceConnectionList.tsx:44-49`) | Custom query: Inherent. No picker: Introduced |
| Slugs (map, connection, dataset) | `mapForm.ts:77`, `sourceForms.ts:125,149` | Derivable from the title. The two validators disagree, and slug `new` collides with `/maps/new` | Introduced |

### 1.3 Input bugs — Introduced, High

1. **The SQL textarea eats input.** Every keystroke trims each line and drops empty lines (`MapConfigurationBuilder.tsx:104-106`). You cannot type a trailing space, so spaces between words disappear as you type, and Enter does nothing. The category-order textarea has the same bug (`ColorEncodingEditor.tsx:213`).
2. **The override-name input loses focus on every character.** Each row is keyed by its own value (`ColorEncodingEditor.tsx:230-231`).
3. **argmax/argmin measures are typed as numeric in the editor** (`web/src/domain/map/colorEncoding.ts:29-31`) but categorical in the viewer (`web/src/features/map-viewer/engine/channels.ts:29-39`). Visible on screen: "dominant_operator_by_tests — computed measure · numeric" next to "Unordered categories".
4. **Saving can silently drop fields.** The editor never carries `reduction`, `aggregate` or `storage` from the loaded draft (`web/src/entities/catalog/model/catalog.ts:157-162` vs `mapForm.ts:44-64,91-106`), and the PUT replaces the whole draft. Latent for the six seeded maps; (unverified) for maps created through the API.
5. **The colour preview mixes real and fake categories.** It always pads the legend with hard-coded `Category 1…4` (`ColorEncodingEditor.tsx:268`).
6. **Changing the dataset keeps the old configuration.** Stale channel references only surface on save (`MapEditorPage.tsx:178`).
7. **The unsaved-changes guard covers only browser unload and Cancel.** "Back to maps" and the other in-app links leave without a prompt (`MapEditorPage.tsx:71-78,148,152`).

### 1.4 No feedback loop — Introduced, High

- There is no map preview while editing.
- Every definition save queues a full bake (editor banner; `web/src/features/map-management/api/mapManagementMutations.ts:118-135`).
- The colour preview runs on an illustrative domain, not the real data.

The cost of a bake is inherent. Not having a draft, a sampled preview, or a "save without baking" option is introduced.

### 1.5 Internals leak to users — Introduced, Medium

- **Developer jargon:** "closed per-geometry measure DSL", "Bake-time row predicates", "source contract", "interaction contract", "control plane", "bounded preview".
- **Raw values:**
  - The .NET TimeSpan `1.00:00:00` shown as the schedule interval.
  - Raw exception text — "Exception of type 'System.OutOfMemoryException' was thrown." — as "Last failure", still shown after a later successful run.
  - An unbounded list of red failure banners in bake history.
  - Release IDs and "N tiles · N cells resident" in the **public** viewer.
- **API errors become JSON-parse errors.** Any unknown `/api/*` route returns the SPA's `index.html` with status 200 (`src/Colossus.Server/Program.cs:130`, `MapFallbackToFile` without an `/api` exclusion). The UI then shows "Unexpected token '<' … is not valid JSON". Reproduced locally: `GET /api/does-not-exist` returns `200 text/html`.
- **`/` is a developer placeholder:** "Manage maps at /maps or open … /maps/geonames".

### 1.6 Journey gaps — Introduced, Medium

- Saving a dataset doesn't lead to creating a map. You get a text message and must navigate yourself.
- Connections and datasets can be created but never edited or archived; the API has create calls only (`web/src/entities/catalog/api/catalogApi.ts:63-67`).
- Archived maps cannot be restored.
- "Open" and "Copy embed" are offered for maps that were never baked, landing the user on a raw "latest manifest 404".
- There is no global navigation; each page injects its own header buttons.

### 1.7 Design system — mixed

- **Keep:**
  - A real token system: `web/src/shared/ui/styles/_tokens.scss` emits CSS variables, with light and dark themes.
  - A small, accessible primitive kit: skip link, native `<dialog>`, focus rings.
- **Introduced, Medium:**
  - The kit is too small. It lacks Tabs, Table, Combobox/multi-select, tag input, stepper, toast, skeleton and an icon set, which is why checkboxes and text boxes appear where pickers belong.
  - Colours and sub-scale font sizes bypass the tokens (16 font sizes below the scale minimum; literal `#0d0d0d`, `#0d1117`).
  - `ViewerPanel` and `EmbedSidebar` are near-identical copies.
  - The numbered-step heading is implemented three times.
- **Brand:** hard-coded red `#e60000`, and Storybook is labelled "Vodafone-inspired" (`web/.storybook/stories/DesignSystem.stories.tsx:48,88`). Confirm brand ownership before more UI is built on it.

### 1.8 What to keep from the UI

- The core of `ColorEncodingEditor`: preset swatches, scale types filtered by channel kind, and a preview that goes through the same legend engine as the published viewer.
- The dataset onboarding pattern: inspect schema → pre-fill a channel table by heuristics → let the user adjust (`sourceForms.ts:56-118`). The map editor should copy this.
- The connection form, which is rendered from adapter descriptors.
- Optimistic concurrency (`expectedRevision`) and partial-save recovery.
- The viewer itself. It works: the map renders, and filters, legend and inspect behave. Its URL state is addressable and the embed `postMessage` contract is versioned.

---

## 2. Frontend architecture

| Finding | Evidence | Label / Severity |
|---|---|---|
| **Two architectures, half merged.** Feature-Sliced layers (`app/pages/widgets/features/entities/shared`) sit beside `domain/` and `infrastructure/` from an unfinished migration. The architecture test enforces layer order only: not slice public APIs, not dynamic `import()`. | `web/src/architecture.test.ts:19,61` | Introduced, High |
| **The engine is one flat folder.** `features/map-viewer/engine` has 22 source files (4,269 lines, ~39% of the frontend's 11,068 non-test source lines). They do eight different jobs, from network fetching to a GLSL shader, side by side in one folder. It is a library with no runtime React imports (only `import type { Theme }`). | `web/src/features/map-viewer/engine/*` | Content Inherent, flat layout Introduced |
| **The viewer page is the application.** `MapViewerPage.tsx` is 533 lines. It imports 15 engine modules, has 15 `isEmbed` branches, and owns picking, deck layers, GPU props, URL sync and embed events. | `web/src/pages/maps/MapViewerPage.tsx` | Introduced, High |
| **God files.** `tileData.ts` 885 lines, `slab.ts` 709, `shared/observability/perf.ts` 543, `measures.ts` 509, `useMeasureFold.ts` 361, `PerfDashboard.tsx` 304, `colorScale.ts` 294. Split plans in [02](02-folder-structure.md). | `wc -l` | Introduced, Medium |
| **Fold maths copied three times on the client.** `DenseAgg` ≈ `InnerAgg`; context compile ×3 inside `slab.ts`; finalizers ×2. That is on top of the server's DuckDB fold and the bake's SQL. | `slab.ts:172,300,480,537,676`, `measures.ts:190,317` | Introduced, High (parity risk) |
| **Duplicated types and constants.** `ChannelSpec`/`GeometrySpec` defined twice with different shapes. Sentinel `2.0e38` ×3, `'(all)'` ×3, `NUMERIC_TYPES` ×2-3, path-alias table ×4. | `domain/map/manifest.ts:12-28` vs `entities/catalog/model/catalog.ts`; `dates.ts`, `gpuFilter.ts`, `measures.ts` | Introduced, Medium |
| **Stringly-typed state.** Filters are `"from..to"` strings with an `(all)` sentinel. The camera is `Record<string, unknown>`. Fetch errors are classified by regex on message text. | `useMeasureFold.ts:48-51,242` | Introduced, Medium |
| **Two data-fetching styles.** TanStack Query for management. Hand-rolled effects for the viewer, with no abort, and a promise with no `.catch` that leaves a silently blank map. | `web/src/features/map-viewer/hooks/useViewData.ts:81-86` | Introduced, Medium |
| **URL state is write-only.** Read once at mount; back/forward does nothing. Two URL schemas own the same keys. | `MapViewerPage.tsx:78`, `embed.ts`, `viewRoute.ts` | Introduced, Medium |
| **Diagnostics in production paths.** `window.__perf` is installed for everyone. The perf dashboard is bundled into the viewer chunk. A temporary harness ships in `dist/`. | `web/src/main.tsx:19-21`, `MapViewerPage.tsx:13` | Introduced, Medium |
| **Tests are thin where bugs live.** Tests are excluded from type-checking. Vitest runs in a Node environment. Nothing covers hooks, `MapViewerPage`, the tile worker, the service worker or remote fold. | `web/tsconfig.app.json:36`, `web/vitest.config.ts:55` | Introduced, High |
| **Broken or dead tooling.** Both bench scripts import `src/lib/*`, which no longer exists. `domain/map/view.ts` is unused. `@luma.gl/core` is imported but not declared. | `web/scripts/bench-*.ts:15-21`, `colorScaleExtension.ts:3` | Introduced, Low |
| **`web/` is not self-contained.** Five test files read `../../../../../tests/fixtures/*.json`. | `geometryCodec.test.ts:17`, `measures.test.ts:21`, `slab.test.ts:40`, `tileDataPack.test.ts:123`, `tiling.test.ts:14` | Introduced, High |
| **Storybook is out of proportion.** Five Storybook packages, browser-mode Vitest run twice, and Playwright visual tests — for four stories. None cover the management forms or the colour editor. | `web/.storybook/` | Introduced, Low |

Keep:

- The zero-copy tile pipeline.
- `TileCache`: framework-free, byte-budgeted, abortable, exposed via `useSyncExternalStore`.
- GPU filter slots, and the colour lookup texture driven by one scale authority shared by CPU, legend and GPU.
- The service-worker cache design.
- The bundle budget gate (`web/scripts/verify-bundle.mjs`).
- The cross-language fixtures.

---

## 3. Backend → frontend contract

This is the most important section for frontend work. The full map is in [05-how-it-works.md](05-how-it-works.md).

- **The split is sound (Inherent, good).** The public viewer reads only static files (`latest.json`, `manifest.json`, tiles, packs), plus an optional `POST /api/views/{slug}/fold`. The management UI uses about 20 JSON endpoints.
- **No code generation (Introduced, High).** Every JSON type is hand-duplicated in C# and TypeScript. OpenAPI is exposed but declares no response types and generates nothing. Several TypeScript fields are typed `unknown`: `filters`, `measures`, `encoding`, `inspect`.
- **No real versioning (Introduced, Medium).** `schemaVersion` exists on `Manifest`, `ViewConfig` and `MapDraft` but is never read by either side. Compatibility rests on `tileFormat` plus "is this field present".
- **The client re-derives what the server knows (Introduced, Medium).** The render column schema and the slab plane names are rebuilt from the view config (`channels.ts:29-59`, `slab.ts:102`, mirroring `EffectiveView.cs` and `MeasurePartials.cs`) instead of being read from the manifest.
- **Little is pinned across languages (Introduced, High).** Four JSON fixtures exist (tiling, measures, geometry codec, slab). Nothing pins the manifest, the Arrow tile bytes, the render pack, the slab block bytes, or the fold response. The TypeScript tile tests build Arrow in JS instead of parsing bytes the C# side wrote. No CI runs the two suites together.
- **Public exposure (Introduced, High).** Confirmed on disk:
  - The public `manifest.json` embeds `view.source.query` (raw SQL) and `source.settings` (`tiles/admin1/<version>/manifest.json`).
  - `facts.parquet` (511 MB for mobile-coverage) is publicly downloadable (`ServeUnknownFileTypes = true`, `src/Colossus.Server/Program.cs:95`).
- **Traps (Introduced, Medium):**
  - `mapId` means a slug in viewer routes and a GUID in management routes.
  - `TimeSpan` travels as `"d.hh:mm:ss"` and is hand-parsed in TypeScript.
  - `API_BASE` is derived by regex from `TILES_BASE` and defaults to `localhost:5174` even in production builds (`web/src/shared/api/config.ts:1-2`).
  - The "legacy" `/api/views` namespace hosts the live fold endpoint.

Inherent and correctly shared:

- The binary formats themselves.
- The measure grammar, parsed identically on both sides.
- The GPU slot rule.
- The geometry codec's dependence on deterministic triangle counts, which is pinned by fixtures.

---

## 4. Backend

| Finding | Evidence | Label / Severity |
|---|---|---|
| **Layering is clean and enforced by a test.** Keep. | `tests/Colossus.Tests/ArchitectureBoundaryTests.cs` | — |
| **The release model and the idempotent bake-run state machine are good engineering.** Keep. | `BakeRunExecutor.cs`, `PreparedReleaseVerifier` | Inherent |
| **`MySqlDatabase.cs` is 905 lines.** Six repositories, hand-rolled migrations run inside a constructor, `$`→`@` parameter rewriting, timestamps as `VARCHAR`, no transactions anywhere. | `src/Colossus.Infrastructure/Catalog/MySqlDatabase.cs:28,510-740,746` | Introduced, High |
| **ClickHouse bakes ignore the catalog connection.** Probe and extract use the default client from appsettings; only preview honours the connection's URL and credentials. A ClickHouse connection created in the UI previews one server and bakes another. | `ClickHouseAdapter.cs:138,159` | Introduced, High |
| **Trino and Parquet copy-paste ~150 lines**, and each scans the source twice (probe and extract each materialise). | `TrinoSourceAdapter.cs`, `ParquetSourceAdapter.cs` | Introduced, Medium (scan cost unverified) |
| **Bakes run inside the API process.** Scheduling fires from three triggers: a 1-second ticker, a minutely job and a startup reconcile. Starting the dev server immediately fires scheduled bakes (observed). | `src/Colossus.Server/Scheduling/*` | Introduced, Medium |
| **Orchestration monoliths.** `BakeViewUseCase.BakeAsync` is 175 lines; `BakeRunExecutor.ExecuteAsync` is 245 lines. | `src/Colossus.Application/` | Stage order Inherent, shape Introduced |
| **Options sprawl and ad-hoc logging.** 13 options classes with 3 binding styles. `Console.*` is used instead of `ILogger` almost everywhere. | `src/Colossus.Infrastructure/Configuration/*` | Introduced, Medium |
| **Two worlds.** Legacy `views/*.json` are imported once, keyed by slug, so editing a JSON file later has no effect. The CLI bakes outside the run/release records. The view/viewId/slug/map vocabulary is muddled. | `ProductCatalogService.cs:148-176`, `src/Colossus.Bake/Program.cs:75-89` | Introduced, Medium |
| **Docker-dependent tests fail instead of skipping.** About 12 test files need Docker and have no trait or skip. An integration test writes fixtures into `docker/trino/data` inside the repo and never cleans up. | `TrinoParquetIntegrationTests.cs:28,60` | Introduced, Medium |

---

## 5. Security

This applies to anything deployed.

- **Open API (Introduced, High).** There is no authentication or authorisation anywhere. CORS allows any origin, method and header (`Program.cs:35-37`). Swagger is served in every environment (`Program.cs:57-63`).
- **What that exposes in the current code:**
  - Dataset queries and bake filters are raw SQL concatenated into source queries. Anyone who can reach the API can run SQL against the warehouse with the stored credentials.
  - Connection URLs are fetched server-side, so `POST /connections/{id}/test` is a server-side request forgery path.
  - Connection secrets are stored in plain text (`secrets_json`, `MySqlDatabase.cs:51`).
  - `FoldController` builds file paths from the route `id` and the body `version` with no validation, no tile cap and no concurrency limit (`FoldController.cs:28-33`).
- **Production today.** `https://colossus.shehabhassan.space` serves the **July** build: its OpenAPI lists only `GET/POST /api/views`, `GET /api/views/{id}`, `GET /api/views/{id}/url`, so the product catalog API is not live there. It does serve public Swagger and an unauthenticated `POST /api/views`. In the July code that endpoint writes a view config file to the server's disk (`ViewsController.Register` at commit `138aac8`). Not exercised. Treat it as live.

---

## 6. Repo, tooling, workflow

- **`web/storybook-static` is local build output.** It is the result of `npm run build:storybook`, is gitignored and was never committed (8.9 MB). It belongs to a larger pattern: build output written next to source. On disk:
  - ~3.5 GB of `bin/obj`, `publish/`, `publish-x86/`, `web/dist`, `.vs`, `.codex-run`.
  - 2.2 GB of git worktrees under `.claude/worktrees`.
  - A stale `src/Colossus.Server/wwwroot` whose `index.html` is titled "web".
- **Runtime data lives in the repo.** `tiles/` is 4.9 GB, of which ~1.9 GB is superseded versions; `staging/` is 1.1 GB. Data paths resolve by walking up the directory tree to the folder containing `Colossus.slnx` (`src/Colossus.Infrastructure/RepoPaths.cs:11`), so moving the solution file silently relocates all data.
- **A crash-looping container.** `colossus-server-dev` is not in compose. It restarts forever (`unless-stopped`), mounting `.codex-run/server-capped/app-numeric-filters` (now missing its DLL) plus the repo's `tiles/`, `staging/` and `views/`.
- **No CI.** `.github/workflows/` is empty.
- **No toolchain pinning.** There is no `global.json`, `.editorconfig`, `.gitattributes` or `Directory.Build.props`.
- **Port drift.** Web: 5173 / 5212. Server: 5174 / 5216 / 7255.
- **History weight.** A 71.6 MB demo video sits in history (`cc58131`) and is reachable from every branch: about 85% of the git pack.
- **Branches.** There are 15. `saga/exact-numeric-filtering` holds unmerged work: a Rust benchmark plus docs. Decide its fate before pruning.
- **Manual deploy.** Hand-copy `web/dist` into `wwwroot`, publish, zip, upload. The only runbooks are stale legacy docs.

---

## 7. Inherent vs introduced — the summary

| Inherent (keep, document, test) | Introduced (simplify or delete) |
|---|---|
| Bake pipeline, out-of-core reduction, LOD tiling | JSON-string form state, typed DSL and SQL as the only UI |
| Binary tile, geometry, slab and pack formats; zero-copy decode | Hand-duplicated JSON contract, no versioning, no golden bytes |
| Measures recomputed per filter (fold); a closed grammar shared by both sides | Fold maths copied 3× client-side; no server/client parity test in CI |
| GPU filter slots, colour lookup texture, worker decode, service-worker cache | 533-line viewer page; engine in one flat folder; diagnostics in production paths |
| Release immutability, retention, run state machine | Hand-rolled migrations, 905-line repository, options sprawl, `Console` logging |
| Multiple source engines (ClickHouse, Trino, Parquet) | Adapter copy-paste, double scans, ClickHouse ignoring the connection |
| Category values exist only after a bake | No preview, no draft, every save is a full bake |
| | No auth; public SQL and facts; build output and runtime data inside the repo; no CI |

---

## 8. Still unverified

Nothing below was executed; treat each as a lead, not a fact.

- The real cost of the double source scan.
- A packed bake's fallback fetch of `0/0/0.arrow` returning 404 (`channels.ts:170`).
- Whether any live release still uses tile formats 1 or 2.
