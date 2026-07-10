# GPU Residency + Group/Measure — Build Report

Single source of build truth. Per-phase status, baseline vs after numbers, acceptance evidence, and
every deviation from the phase docs with rationale. Kept current in git before each commit.

## Environment at start

- Green baseline @ `2b064ec`: `dotnet test` 93 passed; web `npx tsc -b` clean, `oxlint` clean,
  `vitest` 79 passed.
- deck.gl 9.3.6, apache-arrow 21.1.0, React 19, Vite 8.
- Views baked: `geonames` (point, 13.4M), `ookla-fixed` (polygon). Server :5174, web dev :5173.

---

## Phase 0 — Baseline (abbreviated)

Status: in progress.

Method: dev servers started via the preview harness; measured on the GeoNames point view with a
fixed camera. Filter-change wall time = `performance.mark` before the HUD `setFilters` dispatch to
`requestAnimationFrame` after network idle; `.arrow` fetch count from
`performance.getEntriesByType('resource')` delta; recolor = same marking around the color-channel
select.

| Metric | Baseline (before Phase 1) |
|---|---|
| Filter change → settled, wall ms (geonames) | _pending_ |
| `.arrow` fetches per filter change | _pending_ |
| Recolor (color-channel switch) wall ms | _pending_ |

---

## Phase 1 — GPU Filtering

Status: not started.

## Phase 2 — GPU Color

Status: not started.

## Phase 3 — Zero-copy tiles

Status: not started.

## Phase 4 — Group/Measure model

Status: not started.
