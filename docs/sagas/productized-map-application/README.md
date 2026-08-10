# Saga 002 — Productized map application

- **Status:** Active; architecture foundation approved
- **Created:** 2026-08-10
- **Last verified:** 2026-08-10

## Objective

Turn the existing renderer and bake engine into a maintainable application in which an operator can manage
maps, connect ClickHouse or Trino, read raw Parquet datasets, preview a map, schedule recurring bakes through
Hangfire, inspect job history, publish immutable releases, and embed an individual map in another product.

The implementation must leave the fidelity and performance contracts of the current bake and render paths
intact while replacing filesystem/CLI-centered product workflows with explicit application use cases and
durable state.

## Non-goals

- Authentication, authorization, organizations, billing, and collaboration are deferred.
- This saga does not turn Colossus into a general-purpose BI or charting platform.
- It does not replace deck.gl or reopen the deferred custom-GPU work in saga 001.
- It does not require every source to share a SQL dialect. The shared contract is the canonical staging
  result, not the query text used to produce it.
- It does not perform a big-bang rewrite or discard verified tile formats and manifests.

## Target product model

The product separates five concepts that are currently compressed into a view JSON file and local paths:

1. **Connection** — source endpoint and adapter-specific settings.
2. **Dataset** — query or Parquet location plus discovered schema and geometry mapping.
3. **Map** — mark, viewport, channels, measures, filters, encoding, and inspect behavior.
4. **Bake policy** — enabled state, recurrence, time zone, overlap rule, and release retention.
5. **Release** — one immutable, verified tile version and its publication metadata.

The runtime separates into three surfaces:

- the **management/control plane** owns maps, schedules, job history, and releases;
- the **bake worker** probes a source, produces canonical staging data, bakes, verifies, publishes, and
  applies retention;
- the **viewer/data plane** reads one published manifest and immutable artifacts and remains independently
  embeddable.

## Work-item index

| Order | Item | Status | Estimated production work |
| --- | --- | --- | ---: |
| 1 | [Clean architecture foundation](01-clean-architecture-foundation.md) | Active | 4–6 engineering days |
| 2 | [Durable product catalog](02-durable-product-catalog.md) | Candidate | 4–6 engineering days |
| 3 | [Viewer shell and embed contract](03-viewer-shell-and-embed-contract.md) | Candidate | 4–6 engineering days |
| 4 | [Map management application](04-map-management-application.md) | Candidate | 5–8 engineering days |
| 5 | [Source adapter and preview platform](05-source-adapter-and-preview-platform.md) | Candidate | 4–7 engineering days |
| 6 | [Raw Parquet source adapter](06-raw-parquet-source-adapter.md) | Candidate | 4–6 engineering days |
| 7 | [Trino source adapter](07-trino-source-adapter.md) | Candidate | 6–10 engineering days |
| 8 | [Hangfire bake scheduling](08-hangfire-bake-scheduling.md) | Candidate | 5–8 engineering days |
| 9 | [Release publication and retention](09-release-publication-and-retention.md) | Candidate | 5–8 engineering days |
| 10 | [Operational and quality gates](10-operational-and-quality-gates.md) | Candidate | 3–5 engineering days |

The current estimate is 8–12 engineering weeks for one experienced developer, including migration,
integration tests, failure-path testing, and browser verification. Items are ordered to establish the
boundaries before adding adapters or UI against unstable contracts. Viewer-shell work may proceed after
item 01 without waiting for source adapters; the catalog, scheduler, and release work should retain their
listed dependency order.

## Evidence already established

- [`BakeViewUseCase`](../../../src/Colossus.Application/BakeViewUseCase.cs) already orchestrates the core
  bake behind source, reduction, store, packing, and verification seams.
- [`ISourceAdapter`](../../../src/Colossus.Domain/Sources/ISourceAdapter.cs) proves the engine after staging
  can be source-agnostic, though the contract needs discovery/preview/progress capabilities.
- [`FileBakeStore`](../../../src/Colossus.Infrastructure/Baking/FileBakeStore.cs) already writes immutable
  version directories and atomically flips `latest.json`.
- The web client already supports URL-addressable map state and a chromeless iframe mode.
- The dense-map interaction path is browser-verified by saga 001; product work must retain that baseline.

The same evidence exposes the product gaps: [`ViewConfig`](../../../src/Colossus.Domain/Model/ViewConfig.cs)
combines source and presentation concerns, [`ViewRegistry`](../../../src/Colossus.Infrastructure/Views/ViewRegistry.cs)
persists mutable product state as JSON files, the bake host runs synchronously from command-line arguments,
and the current HUD combines map controls, diagnostics, and map selection over the rendering surface.

## Global acceptance gates

- A map can be created and managed without editing JSON or invoking a bake command.
- ClickHouse, Trino, and Parquet all produce the same canonical staging contract and pass adapter contract
  tests against representative point, quadkey, and WKT inputs.
- The normal map page reserves a dedicated map window; filters, legend, inspection, map selection, and
  diagnostics do not obscure it.
- One map can be embedded without management chrome and without loading the map catalog.
- Hangfire uses persistent storage, prevents overlapping bakes for one map, exposes run history, and can
  run immediately or on a configured recurrence.
- A failed or cancelled bake never replaces the published release. Successful publication is atomic.
- Retention never deletes the current release or an active run and demonstrably removes expired versions.
- Existing fidelity, frontend behavior, tile formats, and dense-pan performance remain verified.
- Dependency rules, backend tests, frontend tests, lint, and production builds run in CI.

## Next action

Implement item 01 first. Capture characterization tests around the current bake, manifest, registry, and
viewer bootstrap; establish enforceable dependency rules; then move direct filesystem and host concerns
out of the application layer. Do not add Hangfire, Trino, Parquet, or catalog persistence until those
boundaries are in place.

## Handoff memory

The approved product direction is a source-agnostic scheduled map catalog, not an authenticated SaaS in
this phase. Maps are managed on a dedicated page and viewed one at a time. The full viewer uses application
layout around a dedicated map window; the embed viewer is a separate chrome-free route. Hangfire owns
recurring bake execution, and release cleanup is part of the successful-publication lifecycle.

Preserve the current engine as the migration baseline. Prefer characterization tests, adapters, and
compatibility shims over broad renames or rewrites. Authentication and authorization must not block the
single-operator product path, but the new contracts should avoid choices that make later tenant scoping
impossible.
