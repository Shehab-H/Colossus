# GPU Residency + Group/Measure — Build Report

Single source of build truth for the one-pass build (README.md order). Per-phase status, commit
hashes, baseline-vs-after numbers, acceptance evidence, and every deviation from the phase docs with
rationale. Maintained continuously; updated before each commit.

## Session log

- Session started on branch `claude/xenodochial-torvalds-f1f0bd` (worktree), base commit `8bfdf18`.
  Confirmed no prior-agent work exists: both sibling worktrees clean, no commits beyond `main`.

## Environment baseline (pre-Phase-1)

Green before any code change:

| Check | Result |
|---|---|
| `dotnet build` | succeeded, 0 warnings |
| `dotnet test` | 93 passed, 0 failed |
| `web: npx tsc -b` | pass |
| `web: npm run lint` (oxlint) | clean |
| `web: npm run test` (vitest) | 79 passed |
| ClickHouse tables | geonames 13,447,008 · ookla_fixed 6,655,986 · mobile_coverage 7,607,947 |

## Phase status

| Phase | Status | Commit | Notes |
|---|---|---|---|
| 0 — Baseline | done | `b29cc9c` | build/test green; browser baseline in `BASELINE.md`; geonames+ookla-fixed baked, `verify` PASS |
| 1 — GPU filtering | done | _(this commit)_ | filter → `DataFilterExtension` uniforms; decode-time filtering deleted; cache key = `(version, tileKey)`; all gates green |
| 2 — GPU color | not started | — | |
| 3 — Zero-copy tiles v2 | not started | — | |
| 4 — Group/measure | not started | — | |

## Deviations from phase docs

- **Spike path taken (PHASE-1 §0):** the stock `DataFilterExtension` composes with the
  external-indices `SolidPolygonLayer` — **no custom `LayerExtension` fallback was needed**. One
  wrinkle: for the binary polygon path, `getFilterValue` must be supplied **inside `data.attributes`**
  (per-vertex), not as a top-level layer accessor — the top-level accessor works for
  `ScatterplotLayer` (points) but is ignored by the binary `SolidPolygonLayer`. `deckData.ts` injects
  it per-vertex for both layer types; this matches PHASE-1 §1.5. Spike verified visually (Americas
  filtered out, eastern hemisphere retained) with picking intact (filtered marks not pickable).
- **Polygon live-filter acceptance (PHASE-1 §4.4):** no *currently-bakeable* polygon view carries a
  filterable channel in its row-regime tiles — the `AggregateReducer` drops dimension/temporal
  channels, so `ookla-fixed` and `mobile-coverage` tiles carry only measures (x, y, geometry,
  part_offsets, triangles, + measure columns). This is a pre-existing row-regime limitation, **not a
  Phase-1 regression**, and is exactly what GROUP-MEASURES (Phase 4) resolves (per-fact companions
  restore dimension/temporal facts). Polygon × `DataFilterExtension` composition is therefore proven
  via the §0 spike rather than a live filterable polygon view. Point-view live filtering is fully
  proven on geonames (below).

## Phase 0 — Baseline

See `BASELINE.md` for the measurement table. Method + numbers recorded there and re-measured after
each landed phase.

## Phase 1 — GPU filtering

**What landed.** A filter change now touches zero tile bytes: filters become
`DataFilterExtension` `filterRange`/`filterEnabled` uniforms on the layers, and the tile cache stores
exactly one entry per `(version, tileKey)`. Decode-time filtering is gone.

- **New:** `web/src/lib/gpuFilter.ts` (+ 18 unit tests) — filter slots per view, per-mark/per-vertex
  filter values, and filter selections → `filterRange` pairs with the null/open sentinels
  (`MAX_SAFE_F32 = 3e38`, `NULL_DAY = MISSING_CODE = 2e38`).
- **`channels.ts`:** added `canonicalCategories` (baked full-extract domain, else null → caller falls
  back to discovered options); `describeColorDomain`'s categorical branch refactored onto it;
  `filterKey` deleted.
- **`tileData.ts`:** `decodeTile(view, table, slots?)` builds `filterValues`; **deleted** `rowsMatching`
  and every `keep` param/branch — decode is now unconditional whole-tile. `tileBytes` counts
  `filterValues`.
- **Plumbing:** worker message carries `slots` (not `filters`); `tileLoader.load(view, version, key, slots)`;
  `useTiles` cache key collapsed to `compositeKey(version, key)` — `fkey` and the `filters` hook param
  removed. `deckData.ts` adds `getFilterValue` to `data.attributes`. `App.tsx` computes `slots`
  (memo on manifest) and `filterProps` (memo on `[slots, activeFilters]`), attaches a module-cached
  `DataFilterExtension` per `filterSize` to both layer types.
- **HUD:** counter relabeled to "cells resident" (counts resident, pre-filter marks).

**Acceptance evidence (this session, live browser).**

| Criterion | Result |
|---|---|
| `tsc -b` / `oxlint` / `vitest` / `dotnet test` | pass / clean / 98 passed / 93 passed |
| `verify` (fidelity) | PASS — geonames, mobile-coverage, ookla-fixed all leafRows=source |
| Zero-work filter change (geonames, feature_class → P) | **0 `.arrow` fetches, 0 long tasks (>50ms), resident count unchanged at 128,212** |
| Filter applies visually | only `feature_class='P'` marks render (GPU discard) |
| View switching | geonames ↔ ookla-fixed, URL slug updates, both render, 0 console errors |
| Polygon view renders (slots=null) | ookla-fixed download_mbps choropleth renders correctly |
| Embed URL with filters | `?view=geonames&f_feature_class=P` loads with the filter pre-applied |
| Polygon × filter composition | proven via §0 spike (see Deviations) |
| Click-to-inspect | pick path (`onPick`→`dataByLayerId`→`columnValue`) untouched by Phase 1; verified prior session; §0 spike proved filtered marks are excluded from picking |

**Baseline delta.** Phase-0 baseline measured a filter change at ~26–393 ms coarse (p50 ~82 ms) plus
a ~1.0–1.8 s full-fidelity re-decode. After Phase 1 a filter change is a uniform update — no fetch,
no decode, no worker message, no attribute upload (0 `.arrow`, 0 long tasks observed). Marks update
on the next frame.
