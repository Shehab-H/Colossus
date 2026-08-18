# Saga 002 — Productized map application

- **Status:** Active; Hangfire bake scheduling complete, release publication and retention is next
- **Created:** 2026-08-10
- **Last verified:** 2026-08-18

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
- It does not replace deck.gl or revive the rejected GPU experiments archived from saga 001.
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
| 9 | [Release publication and retention](09-release-publication-and-retention.md) | Candidate | 5–8 engineering days |
| 10 | [Operational and quality gates](10-operational-and-quality-gates.md) | Candidate | 3–5 engineering days |

The current estimate is 8–12 engineering weeks for one experienced developer, including migration,
integration tests, failure-path testing, and browser verification. Items are ordered to establish the
boundaries before adding adapters or UI against unstable contracts. Viewer-shell work may proceed after
item 01 without waiting for source adapters; the catalog, scheduler, and release work should retain their
listed dependency order.

Completed foundation: [item 01 — clean architecture foundation](../../archive/2026/2026-08-10-productized-map-application/01-clean-architecture-foundation.md)
established and verified the backend and frontend dependency boundaries on 2026-08-10.

Completed catalog: [item 02 — durable product catalog](../../archive/2026/2026-08-10-productized-map-application/02-durable-product-catalog.md)
established the relational product model, migration, compatibility lookup, and secret-safe APIs. The approved
[MySQL and appsettings follow-up](../../archive/2026/2026-08-10-productized-map-application/02a-mysql-and-appsettings-configuration.md)
replaced the initial SQLite adapter before further product work.

Completed viewer shell: [item 03 — viewer shell and embed contract](../../archive/2026/2026-08-10-productized-map-application/03-viewer-shell-and-embed-contract.md)
established canonical single-map routes, a responsive application layout around a dedicated map window,
and a versioned cross-origin iframe contract without map-catalog loading.

Completed map management: [item 04 — map management application](../../archive/2026/2026-08-10-productized-map-application/04-map-management-application.md)
established the `/maps` product home, durable create/edit/detail workflows, structured validation,
run/schedule controls, browser-verified archival, and management read models for future job and release state.

Completed frontend foundation: [item 04a — frontend design system and UI architecture](../../archive/2026/2026-08-14-productized-map-application/04a-frontend-design-system-and-ui-architecture.md)
established the Vodafone-inspired Sass design system, enforced product layers, component workbench, React
Router data routes, TanStack Query ownership, route-lazy viewer, and executable initial-bundle contract.

Completed source platform: [item 05 — source adapter and preview platform](../../archive/2026/2026-08-14-productized-map-application/05-source-adapter-and-preview-platform.md)
established adapter-declared capabilities, secret-safe connection validation and discovery, bounded canonical
preview, extraction progress and cancellation, structured source errors, real ClickHouse contract coverage,
and the `/sources` management workspace.

Completed raw Parquet adapter: [item 06 — raw Parquet source adapter](../../archive/2026/2026-08-14-productized-map-application/06-raw-parquet-source-adapter.md)
established configured local/HTTP/S3-compatible provider aliases, safe file/glob/partition resolution,
fingerprinted immutable inputs, bounded DuckDB previews, canonical point/quadkey/WKT extraction, and
byte-identical tile fidelity against ClickHouse.

Completed Trino adapter: [item 07 — Trino source adapter](../../archive/2026/2026-08-14-productized-map-application/07-trino-source-adapter.md)
established direct paginated statement execution, session state, bounded retry and cancellation, structured
warnings/errors, deterministic typed geometry extraction, row/byte progress, and byte-identical fidelity
against direct Parquet through a pinned Trino/Iceberg integration fixture.

Completed bake scheduling: [item 08 — Hangfire bake scheduling](../../archive/2026/2026-08-14-productized-map-application/08-hangfire-bake-scheduling.md)
established application-owned immutable run capture and execution, Hangfire persistence in the application MySQL database,
recurrence reconciliation and missed-interval coalescing, crash-safe per-map exclusion, active cancellation,
transient-only retry, durable progress/metrics/failures, and live management history.

## Evidence already established

- [`BakeViewUseCase`](../../../src/Colossus.Application/BakeViewUseCase.cs) already orchestrates the core
  bake behind source, reduction, store, packing, and verification seams.
- [`ISourceAdapter`](../../../src/Colossus.Application/Abstractions/Sources/ISourceAdapter.cs) proves the engine after staging
  can be source-agnostic, though the contract needs discovery/preview/progress capabilities.
- [`FileBakeStore`](../../../src/Colossus.Infrastructure/Baking/FileBakeStore.cs) already writes immutable
  version directories and atomically flips `latest.json`.
- Item 10 now carries the current single-host Ubuntu deployment checklist, including build, persistent
  storage, systemd, Nginx/TLS, security, backup, smoke-test, upgrade, and rollback gates.
- The web client already supports URL-addressable map state and a chromeless iframe mode.
- The web client now has enforced `app`, `domain`, `features`, `infrastructure`, and `shared` boundaries while
  retaining the verified viewer behavior.
- The durable catalog now separates connections, datasets, maps, bake policies, bake runs, and releases;
  all six legacy views import idempotently into equivalent bake snapshots.
- The full viewer and embed viewer now resolve exactly one release at canonical routes; controls, legend,
  inspection, release details, and opt-in diagnostics stay outside the map canvas at desktop and mobile breakpoints.
- The renderer now uses direct deck.gl tile-cover admission again; the rejected staged-admission experiment
  and its measurements remain only in the archive.

The catalog, management, viewer, and source-platform contracts are stable. Management, full viewer, and embed surfaces now use
the shared Vodafone-inspired Sass design system and enforced page/widget/feature/entity boundaries. The
Storybook workbench enforces interactions, accessibility, and reviewed visual baselines in both themes;
React Router and TanStack Query now own navigation and remote product state, and the viewer remains isolated
behind a verified lazy boundary. ClickHouse, raw Parquet, and Trino now pass the shared discovery,
bounded-preview, canonical extraction, progress, cancellation, error, and cross-adapter tile-fidelity
contracts. Normal bakes now run through the application-owned persistent queue; the synchronous bake host remains
only as a developer and repair path.

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

Begin item 09: make release preparation/publication an isolated, verified, idempotent workflow and apply safe
retention without deleting the current release or active work.

## Handoff memory

The approved product direction is a source-agnostic scheduled map catalog, not an authenticated SaaS in
this phase. Maps are managed on a dedicated page and viewed one at a time. The full viewer uses application
layout around a dedicated map window; the embed viewer is a separate chrome-free route. Hangfire owns
recurring bake execution, and release cleanup is part of the successful-publication lifecycle.

Preserve the current engine as the migration baseline. Prefer characterization tests, adapters, and
compatibility shims over broad renames or rewrites. Authentication and authorization must not block the
single-operator product path, but the new contracts should avoid choices that make later tenant scoping
impossible.
