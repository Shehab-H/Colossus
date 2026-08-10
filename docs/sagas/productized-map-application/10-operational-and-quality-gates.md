# 10 — Operational and quality gates

- **Status:** Candidate
- **Estimate:** 3–5 engineering days
- **Depends on:** Items 03–09

## Motivation

A scheduled data application needs repeatable builds, visible failure states, resource guardrails, and
recovery procedures. The repository currently has strong unit suites but no active CI workflow and relies
on manual deployment and smoke testing.

## Goal

Make the single-operator product diagnosable and safe to operate without introducing authentication or a
full observability platform.

## Integration surface

- New workflows under [`.github/workflows`](../../../.github/workflows) and the existing backend/frontend
  test commands.
- Server health, logging, and diagnostics configuration in
  [`Colossus.Server`](../../../src/Colossus.Server).
- Catalog, Hangfire storage, source adapters, and artifact-store health boundaries from earlier items.
- Job/release operational actions on the management pages from item 04.

## Required behavior

- CI runs backend build/tests, architecture tests, frontend tests, lint, type-check/build, and Markdown-link
  validation on every change.
- Health endpoints distinguish process liveness, catalog readiness, Hangfire storage readiness, and
  artifact-store readiness without executing a source query.
- Structured logs carry map id, run id, release id, source adapter, stage, duration, and error category.
- Job metrics expose queue latency, stage duration, success/failure/cancellation, rows, tiles, and bytes.
- Preflight checks cover writable staging, artifact-store access, and configurable disk-space headroom.
- Operators can retry, cancel, inspect failure details, and reconcile abandoned runs/releases from the UI.
- Production defaults hide Swagger and the performance dashboard unless explicitly enabled.
- A documented backup/restore procedure covers catalog and Hangfire persistence; immutable releases can be
  reconstructed or reconciled from object storage.

## Main risks

- Health checks that query external datasets can make the app unhealthy whenever a source is unavailable.
- Logging raw SQL, row samples, headers, or connection settings can leak source data and credentials even in
  a single-user deployment.
- High-cardinality per-tile metrics can overload the chosen metrics sink.

## Acceptance evidence required

- CI is green from a clean checkout and blocks a deliberate dependency-rule or link failure.
- Restart, unavailable source, unavailable artifact store, low disk, cancelled bake, and failed publication
  drills produce distinct actionable states.
- Logs contain correlation identifiers and exclude configured secrets and sample-row contents.
- A release checklist verifies map management, all three adapters, scheduling, retention, full viewer,
  embed viewer, fidelity, and dense interaction performance.
