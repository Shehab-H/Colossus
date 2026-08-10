# 01 — Clean architecture foundation

- **Status:** Complete
- **Completed:** 2026-08-10
- **Estimate:** 4–6 engineering days

## Motivation

The repository already has Domain, Application, Infrastructure, and host projects, but several boundaries
are only conventional. Application use cases still read file metadata and files directly, source adapters
are constructed through a switch, mutable map state is represented by a file registry, and the frontend
root composes viewer behavior with product controls. Adding two sources, durable jobs, and release retention
without first tightening these seams would spread source, storage, and scheduler conditionals across the
codebase.

## Goal

Establish and enforce an incremental clean-architecture baseline before product features land:

- **Domain** owns pure map, dataset, geometry, channel, bake-policy, and release invariants.
- **Application** owns use cases and ports. It must not depend on ASP.NET, Hangfire, concrete databases,
  the environment, the console, or direct filesystem I/O.
- **Infrastructure** implements source, persistence, artifact, clock, and scheduling ports.
- **Hosts** own configuration, dependency injection, transports, and process lifetime.
- **Web features** separate reusable viewer state/rendering from management routes and API clients.

## Integration surface

- [`BakeViewUseCase`](../../../../src/Colossus.Application/BakeViewUseCase.cs) and
  [`VerifyFidelityUseCase`](../../../../src/Colossus.Application/VerifyFidelityUseCase.cs).
- Ports under [`Colossus.Application/Abstractions`](../../../../src/Colossus.Application/Abstractions).
- [`ServiceCollectionExtensions`](../../../../src/Colossus.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)
  and both current hosts.
- [`App.tsx`](../../../../web/src/app/App.tsx), the
  [`map-viewer`](../../../../web/src/features/map-viewer) feature, shared domain types, and API infrastructure.

## Implementation boundaries

1. Add characterization tests for the existing ClickHouse bake orchestration, manifest publication,
   fidelity verification, registry behavior, and viewer boot URL.
2. Record the allowed project dependency graph and enforce it with architecture tests.
3. Put use-case ports with the application boundary; keep domain types free of persistence semantics.
4. Replace `File`, `FileInfo`, `Directory`, environment, and console access inside Application with narrow
   ports or returned progress/result events.
5. Replace manually constructed adapter switches with dependency-injected adapter registration keyed by a
   stable adapter identifier.
6. Split frontend folders into viewer, management, shared domain, and API infrastructure without changing
   pixels or routes yet.
7. Delete dead scaffold assets and obsolete code only after reference searches and tests prove they are
   unused. Do not mix behavior changes into mechanical moves.

This item does not introduce a database, Hangfire, Trino, or Parquet. It prepares stable seams for those
items and leaves compatibility adapters for the current view JSON and CLI host until their replacements
are verified.

## Main risks

- A broad namespace/folder rewrite can consume time without improving boundaries. Every move must remove a
  demonstrated dependency violation or enable a listed product item.
- Moving ports can create circular references if transport and domain models are not separated first.
- Characterization tests must pin behavior rather than private implementation structure.
- Frontend extraction can accidentally change deck layer identity and regress staged GPU admission.

## Acceptance evidence required

- An architecture decision in this file is reflected by project references and automated dependency tests.
- Domain and Application contain no direct filesystem, console, environment, ASP.NET, Hangfire, or concrete
  source-client calls.
- Existing ClickHouse bake output and manifest serialization remain fixture-compatible.
- Current backend tests, frontend tests, lint, and production build pass.
- Browser parity covers a dense polygon map, a point map, filters, picking, and the existing embed path.
- A final dependency inventory lists any intentional exception with an owner and removal item.

## Final outcome

The dependency direction is now executable rather than conventional. Domain contains only model and policy;
Application owns bake, reduction, view-catalog, and source ports; Infrastructure supplies their concrete
implementations; and the hosts own dependency injection, configuration, console output, and process lifetime.
Application bake progress is returned as typed events, filesystem operations are behind `IBakeStore`, and
source adapters are discovered from dependency injection by a stable, case-insensitive identifier.

The frontend now has explicit `app`, `domain`, `features`, `infrastructure`, and `shared` boundaries. The app
module is a thin composition root, the existing renderer is isolated as the `map-viewer` feature, manifest and
view types are transport-free domain data, API and cache behavior live in infrastructure, and import aliases
make boundary direction visible. Architecture tests enforce both the allowed imports and removal of the former
flat scaffold. Unreferenced Vite demo assets and styles were deleted after reference checks.

## Verification evidence

- Backend characterization and architecture suite: `dotnet test Colossus.slnx --no-restore` — 159 passed.
- Frontend characterization and boundary suite: `npm test -- --run` — 18 files and 184 tests passed.
- Frontend lint: `npm run lint` — passed.
- Production build: `npm run build` — passed; the pre-existing large-chunk advisory remains informational.
- Browser parity on the running local stack:
  - `mobile-coverage` rendered eight dense polygon tiles and 500,151 resident cells; selecting operator `apex`
    retained full-fidelity rendering;
  - `geonames` rendered a point dataset with 13,447,008 total cells and 201,579 resident cells;
  - picking returned the expected feature detail panel, keyboard panning redrew the viewport, and `?embed=1`
    preserved the compact viewer without catalog or diagnostics chrome.
- Direct source scans and automated architecture tests found no filesystem, console, environment, ASP.NET,
  Hangfire, or concrete source-client use in Domain or Application.

## Intentional dependency inventory

| Exception retained | Why it remains now | Owner and removal item |
| --- | --- | --- |
| Application ports exchange opaque artifact path strings with file-oriented reducers and packers. | Changing the verified DuckDB/Arrow staging pipeline is outside a boundary-only refactor. | Artifact abstraction and immutable release work in item 09. |
| `IViewCatalog` still exposes the file-backed `ViewConfig` compatibility shape. | Existing JSON maps and server routes remain the migration baseline. | Durable product catalog in item 02. |
| `Colossus.Bake` remains a synchronous developer CLI host. | It preserves current operator compatibility while background execution is introduced. | Hangfire scheduling in item 08. |
| Renderer internals remain cohesive inside one `map-viewer` feature rather than being rewritten into new rendering primitives. | Layer identity, staged GPU admission, and tile behavior were deliberately kept pixel-compatible. | Viewer shell/API extraction in item 03; adapter preview contracts in item 05. |
