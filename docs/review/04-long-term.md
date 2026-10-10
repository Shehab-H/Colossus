# 4. Long-term goals

These goals are grouped into three phases. Phase A makes the frontend/backend boundary safe; B fixes the product; C pays down the engine and backend debt. Within a phase, items can run in parallel unless a dependency is noted.

## Phase A — make the boundary safe

### A1. Contract-first boundary

**Outcome:** the frontend can change without reading C#, and either side breaking the other fails CI, not production.

- Create `contracts/` ([02](02-folder-structure.md)):
  - an OpenAPI snapshot;
  - a JSON Schema for the manifest and map definition;
  - written format specs (salvaged from `docs/legacy`, then verified against code);
  - golden fixtures.
- Generate the TypeScript API client and types from OpenAPI: the types land in `types/`, the client in `api/` ([02](02-folder-structure.md)). They replace the hand-written mirrors (`entities/catalog/model/catalog.ts` before the restructure; `types/map.ts`, `bake.ts` and `source.ts` after it).
- Golden binaries written by the C# side, parsed by the TypeScript tests: an Arrow tile in each format, a geometry-codec payload, a render-pack block, a slab block and a fold response.
- **Versioning that is actually checked.** A manifest schema version and a tile format version. The client refuses unknown majors with a readable error. Document every field the client gates on (`renderPack`, `companionSlab`, `foldRoute`, …).
- **Move derived knowledge into the manifest:** the render column schema, slab plane names, the GPU slot rule, shared constants, and the temporal storage unit. The client then stops re-deriving them from the view config.
- One identity rule: management uses GUIDs, viewers use slugs, and the names say which (`mapId` vs `mapSlug`).

### A2. Security model

**Outcome:** nothing deployed is writable or readable by strangers.

- Authentication against the organisation's identity provider, with roles: viewer, operator, admin.
- Public viewer endpoints stay anonymous; everything under management requires a role.
- Secrets encrypted at rest. Bakes get credentials through a secret-resolver port, not plain settings.
- Remove raw SQL from anything public. Long-term, filters cross the wire as a typed predicate AST, and only the server renders SQL (see B2).
- Guard against server-side request forgery on connection URLs (allow-list hosts per environment).
- Rate limits on fold and preview.

### A3. CI/CD and reproducible environments

**Outcome:** one command to run locally, one pipeline to ship.

- CI jobs:
  - frontend lint/test/build;
  - backend unit and integration tests;
  - Docker-tagged tests against compose services;
  - a contract job that runs both sides' fixture suites;
  - an end-to-end smoke test that bakes a small seeded dataset and loads it in a browser.
- Scripted deploy to replace the manual copy, zip and upload; production config from a template plus environment variables.
- Runtime data under an explicitly configured root, never the repo.

### A4. Docs

**Outcome:** a new frontend developer is productive from the docs alone.

- Promote [05-how-it-works.md](05-how-it-works.md) into `docs/architecture/`.
- Add `docs/operations/local-development.md`.
- Write the root `README.md`, `CLAUDE.md` and `AGENTS.md`.
- Keep decisions as short ADRs in `docs/architecture/decisions/`.

### A5. Frontend folder restructure (done 2026-10-10)

**Outcome:** a newcomer can find any part of the frontend from its folder names alone.

- Plain feature folders, small single-job files, and the engine split into eight pipeline-stage folders. Full layout and file mapping in [02](02-folder-structure.md).
- Structure only: no behaviour change, with every check green after each step.
- Done and reviewed on 2026-10-10, including the review's follow-ups; results in [02](02-folder-structure.md#status).

## Phase B — fix the product

### B1. Map Studio

**Outcome:** configuring a map is choosing, not typing, and you see the result before you commit to a bake.

- **Split-pane editor:** structured sections on the left, each with its own validity state; a live preview on the right.
- **Live preview** from a sampled extract or a single-tile "draft bake", so authors see colour, filters and inspect without a full bake.
- **"Save draft" and "Publish (bake)" become separate actions.**
- **Typed form model** as the single source of truth. No JSON strings anywhere in form state; the "advanced JSON" view becomes read-only (view and copy).
- **Server validation returns field paths** (`measures[2].expr`) rather than exception messages matched by substring (`ProductCatalogService.cs:199-206`).
- **Categories come from data:** the last bake's `channelDomains`, or a preview query, feed category pickers, ordering and overrides.

### B2. Structured builders

- **Measure builder:** function → channel(s) filtered by role → optional "where dimension = value". It shows the generated expression and validates it live.
- **Filter (predicate) builder:** column → operator chosen by type → value picker. A "custom SQL" escape hatch exists for admins only.
- **Filter controls** use the same components as the viewer, so defaults are set with the control the end user will see.

### B3. Source onboarding

- **Wizard:** connection → test → pick a table (from discovery) → inspect schema → geometry and channels with human labels → preview → "create map from this".
- Connections and datasets become editable and archivable (the API is create-only today).

### B4. Design system completion

- Add the missing components: Tabs, Table, Combobox / multi-select, tag input, stepper, switch, toast, skeleton, and an icon set.
- Enforce tokens: lint rules ban raw colours and off-scale font sizes.
- Settle the brand question (the current red and the "Vodafone-inspired" Storybook labels) before more UI is built on it.
- Storybook either covers the management forms and the colour editor, or shrinks to a design-system showcase.

### B5. Clean public viewer

- The public viewer shows title, legend, filters and inspect. Release IDs, tile counts and diagnostics move to the management detail page or behind `?perf=1`.
- URL state becomes two-way, so back and forward restore the view.

## Phase C — engine and backend

### C1. Engine and viewer cleanup (frontend)

The folder restructure ([02](02-folder-structure.md)) moves files and splits them without changing behaviour. This step changes the logic itself:

- `MapViewerPage` becomes two thin pages, `MapViewerPage` and `MapEmbedPage`, over `useViewerLayers`, `useViewerPicking`, `useViewerUrl`, `useEmbedBridge` and `<MapCanvas>`. The 15 `isEmbed` branches disappear.
- **One fold implementation on the client.** The fold maths is copied three times today: `InnerAgg`, `DenseAgg`, and context compilation repeated inside `slab.ts`. After the restructure, `fold/aggregators.ts` holds the copies side by side; a shared aggregator interface then replaces them. The existing fixtures prove byte parity.
- `engine/fold/foldCoordinator.ts`, a plain class, replaces the hand-rolled async state machine in `useMeasureFold`. The hook only subscribes to it.
- Tests where the bugs have lived: hooks in jsdom, a worker harness, service-worker URL rules, and Playwright smoke tests for viewer and embed.
- The manifest is loaded through TanStack Query (one fetching style). Filter state is a typed model instead of `"from..to"` strings.

### C2. Backend restructure

- **Persistence:**
  - versioned migrations (DbUp or FluentMigrator) instead of constructor DDL;
  - one repository per aggregate;
  - transactions where several rows change;
  - `DATETIME(6)` instead of string timestamps;
  - set-based reads for the map list (no N+1).
- **Bake worker in its own process.** The API only enqueues. A single scheduling mechanism replaces the 1-second ticker, the minutely job and the startup reconcile.
- **Source adapters:**
  - a shared `Sources/Common` (canonicalisation, SQL helpers, error classification);
  - ClickHouse bakes honour the catalog connection;
  - Trino and Parquet stop scanning the source twice;
  - Parquet canonicalisation moves into DuckDB SQL.
- **Orchestration as stage objects** (`Probe`, `Extract`, `Plan`, `Reduce`, `Pack`, `Verify`, `Publish`) and a `RunStateMachine`, instead of two 200-line methods.
- **One options-binding helper with validation;** `ILogger` everywhere.

### C3. Retire the legacy world

- Move fold to `/api/maps/{slug}/fold`. Then delete `/api/views*`, `IViewCatalog`, the legacy importer and `views/`.
- The CLI bakes through the run/release model, or is deleted.
- Retire tile formats 1 and 2, the row-form companions, and the `Rendering:PackTiles` gate once every live release is re-baked. Check which formats are live first (unverified today).
- Decide `workspace_key`: implement multi-workspace support (tile paths are keyed by slug alone and would collide), or remove it.

## Dependencies at a glance

```
A1 contracts ──► C1 engine and viewer cleanup (types come from contracts)
A5 folder restructure ──► C1 engine and viewer cleanup (logic changes land in the new folders)
A1 contracts ──► B1 Map Studio typed model (map-definition schema)
A2 security  ──► any redeploy of the product catalog API
A3 CI        ──► everything after it (it is the safety net)
B2 predicate AST ──► A2 "no raw SQL in public"
C2 worker split  ──► B1 draft bake (preview bakes need isolated capacity)
C3 move fold     ──► deleting /api/views
```
