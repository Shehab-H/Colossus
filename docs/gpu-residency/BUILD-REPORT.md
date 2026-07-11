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
| 1 — GPU filtering | done | `1157d91` | filter → `DataFilterExtension` uniforms; decode-time filtering deleted; cache key = `(version, tileKey)`; all gates green |
| 2 — GPU color | done | `c123749` | color → LUT texture + `getScaleValue` attribute; `markColors`/CPU recolor deleted; all gates green + live recolor verified |
| 3 — Zero-copy tiles v2 | done | _(this commit)_ | tile format v2 (global triangles, no-null, canonical dicts, f32 measures) + client view-based decode; gates green; fresh bake + verify PASS; view residency proven live |
| 4 — Group/measure | in progress | — | §1 config model + parser + validation done (below); §2–8 pending |
| 4-fetch — Fetch locality | not started | — | 4.1 SW cache + 4.2 prefetch in scope; 4.3 pack container deferred (owner gate) |

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

## Phase 2 — GPU color

**What landed.** Recoloring no longer touches per-mark data. The color scale is sampled into a small
RGBA8 LUT texture + uniforms (`domain`/`transform`/`kind`/`unknownColor`/`lutWidth`); a per-mark
`getScaleValue` attribute (numeric column by reference for points; per-vertex expansion / canonical
codes for polygons) uploads once per (tile, channel) and is reused across every scale/theme.
`markColors` and the per-vertex RGB expansion are deleted.

- **New:** `web/src/lib/colorLut.ts` (+ `colorLut.test.ts`, 7 parity tests) — `buildColorLut(spec, domain)`
  sampling `colorScale.ts` at texel centers; `web/src/lib/colorScaleExtension.ts` — `LayerExtension`
  owning the LUT texture, uniforms, `getScaleValue` attribute, and the `DECKGL_FILTER_COLOR` shader hook.
- **`deckData.ts`:** color paths deleted; `getScaleValue` built lazily per (tile, channel), memoized on
  `channel` only (scale changes no longer touch the data object).
- **`App.tsx`:** constant `getFillColor`; `[dataFilter, colorScale]` extensions on both layer types;
  `buildColorLut` memoized on `[colorSpec, domain]`. `colorScale.ts`/`useViewData.ts` adjusted for LUT sourcing.

**Acceptance evidence (this session).**

| Criterion | Result |
|---|---|
| `tsc -b` / `oxlint` / `vitest` / `dotnet test` | pass / clean / 107 passed / 93 passed |
| LUT parity tests (§1) | in place — `colorLut.test.ts` asserts texels == `colorOf` at texel centers, per scale type + categorical/unknown |
| Live render, no errors | geonames (point) + ookla-fixed (polygon) render; 0 console errors across all interactions; WebGL context never lost |
| Categorical→numeric recolor (feature_class→population→elevation) | color-by switches, shader relinks clean; **residency unchanged at 128,212 cells / 4 tiles** — zero re-decode, zero `.arrow` refetch (§3 zero-data recolor) |
| Polygon per-vertex `getScaleValue` path | ookla-fixed `download_mbps` choropleth renders via the vertex-expansion path, context stable, 0 errors |
| Recolor wall time (§4) | ≤ 1 frame by construction — recolor is now a texture/uniform update with no fetch/decode (residency proof above) |
| Inspect/legend/embed (§5) | inspect/`columnValue` path untouched by Phase 2 (test-covered); view switch updates URL slug correctly |

**Note:** pixel-parity (§2) is proven at the LUT level by `colorLut.test.ts` (GPU samples the same
`colorScale.ts` the CPU legend uses); on-canvas screenshot capture times out on this WebGL/MapLibre
canvas (harness quirk, see `BASELINE.md`), so live parity was checked via clean render + zero-error
recolor across categorical/numeric/negative-numeric channels rather than pixel diff.

## Phase 3 — Zero-copy tiles (format v2)

**What landed.** A tile is one contiguous ArrayBuffer for its whole client life; decode is header
parsing + typed-array views into it — no column copies, no geometry slices, no triangle rebase. Gated
on `manifest.tileFormat: 2`; the format-1 copy path stays for older bakes.

- **Bake:** `Manifest.TileFormat` (=2, set in `BakeViewUseCase`); `ArrowTileWriter` rebases triangle
  indices to tile-global (running vertex base) and marks the triangles field non-nullable, throwing on
  a null polygon geometry; measures cast to `REAL` with null→`NaN` in both reducers (`QuadtreeLodReducer`
  `REPLACE`, `AggregateReducer` `COALESCE`); dimension/identity strings coalesce to `'null'` in the
  ClickHouse extract; `ArrowColumnBuilder.DictionaryScalar` pre-seeds the canonical domain order (domains
  now scanned before reduction in `BakeViewUseCase` and threaded via `ReductionContext.CanonicalDictionaryOrders`),
  so tile codes are the client's canonical codes.
- **Client:** `fetchArrowTable` returns `{table, buffer}`; `tileData.decodeTile` gains a format-2 branch
  (`decodeTileV2` + view helpers) — measures/dict-codes/utf8/geometry/triangles as views, only
  `polyStartIndices`, point positions, utf8 offsets, and `filterValues` built; `TileData.buffer` retained;
  `transferable`/`tileBytes` handle the shared buffer; `tileFormat` plumbed through worker/loader/`useTiles`.

**Acceptance evidence (this session, real re-baked v2 tiles).**

| Criterion | Result |
|---|---|
| `tsc -b` / `oxlint` / `vitest` / `dotnet test` | pass / clean / 110 passed / 94 passed |
| Fresh bake (all 3 views) + `verify` (§1) | PASS — geonames/mobile-coverage/ookla-fixed re-baked to v2, leafRows=source, tile shapes identical to Phase 0 baseline |
| Manifest served as format 2 | `manifest.tileFormat === 2` for all views |
| View residency (§2), polygon (ookla-fixed) | `polyPositions`, `polyTriangles`, all 3 measures all satisfy `.buffer === tile.buffer`; triangles tile-global and in range (maxIdx 37108 < 37110 verts) |
| View residency (§2), point (geonames) | `population` (f64→f32), `elevation` (i32→f32), `feature_class`/`feature_code`/`country_code` dict codes, `name` utf8 — all views into the one buffer; point positions correctly built (not a view) |
| Value correctness through views | dict codes decode to real classes `["L","H","T"]`; utf8 names decode `["Curichi Dos",…]` — canonical dict order + utf8 offset rebase correct |
| Render parity (§4) | point + polygon render, HUD counts identical to baseline (128,212 / 55,068), 0 console errors, WebGL context never lost |
| GPU filter on v2 columns | `feature_class=P` applies GPU-side — residency unchanged (128,212), 0 `.arrow` fetches, 0 errors |
| Mixed-format (§5) | format-1 copy path retained + unit-tested (`decodeTile` default branch); all local views now v2 so no live mix, fallback proven by tests |

**Deviations from PHASE-3 doc.**

- **Non-nullable fields (§2.2 / T6):** the no-null *contract* is enforced by normalization (strings→`'null'`,
  measures→`NaN`) plus a loud throw on null polygon geometry and a non-nullable `triangles` field. Per-channel
  `nullable:false` flags on data columns were **not** threaded through `ArrowColumnBuilder`: the writer has no
  role information (it sees column names/types, not measure-vs-temporal), and the client's functional gate is a
  per-column `nullCount` check (it never reads the field's nullable flag), so a normalized column with
  `nullCount == 0` is viewed regardless. Temporal columns stay nullable (not viewed as zero-copy; `filterValues`
  is rebuilt per filter). This enforces the substance (viewed columns are null-free) without risking a spurious
  bake failure on a stray temporal null.
- **No-null string test (§2.3):** the `COALESCE(…, 'null')` lives in ClickHouse SQL (no live-ClickHouse unit
  test), so it is covered by the fresh re-bake + `verify` + the live render (names/classes decode correctly)
  rather than a DuckDB round-trip. The canonical-dictionary-order and tile-global-triangle writer changes have
  new `dotnet` round-trip tests.

## Phase 4 — Group/measure model

Executing GROUP-MEASURES.md v0 (the "In" column); the "Out (deferred)" column — GPU fold (5.3),
keyed wkt/geohash/h3, point marks, server DuckDB fold, quadtree/raw group regime — stays unbuilt.

### §1 — Config model + parser + validation

**What landed.** The measure grammar (VIEW_CONFIG §4) as a pure Domain module, wired into config
validation. No bake or client behavior changes yet — a view with no `measures` block is byte-for-byte
as before (all row-regime checks unchanged; the new path is skipped when `HasMeasures` is false).

- **New `Colossus.Domain.Measures`:** `MeasureExpr` AST (`Sum|Count|Avg|Wavg|Min|Max` under `Agg`
  with optional `Where`; `Share(inner, whereCh, whereVal)`; `ArgExt(dim, inner, isMax)`) +
  `MeasureParser` (recursive-descent tokenizer/parser, syntax only, errors name the offending token)
  + `MeasureParseException`.
- **`ViewConfig`:** `MeasureSpec(Name, Expr)` record; `Measures?` list; `HasMeasures`. `Validate()`
  now parses every measure and applies the config-time subset of §11: measure-name uniqueness + no
  channel collision, numeric verb args, dict `dim`/`where` channels, keyed geometry (v0 quadkey),
  and `encoding.color`/`inspect` resolving against channels ∪ measures. Data-dependent rules
  (perFact classification, companion budget) are deferred to bake §2–4.
- **Cross-language fixture:** `tests/fixtures/measure-cases.json` (parse cases pin the AST, error
  cases pin the message substring) — the third shared authority alongside tiling and schema. The C#
  side (`MeasureParserTests`, `JsonNode.DeepEquals` on a canonical description) pins it now; the web
  mirror (§7) will pin the same file.

**Acceptance evidence.**

| Criterion | Result |
|---|---|
| `dotnet build` (Domain) | succeeded, 0 warnings |
| `dotnet test` (full suite) | 106 passed (was 93) — +2 parser fixture, +11 validation |
| Row-regime unchanged | `Valid().HasMeasures == false`; no measure code runs; existing 93 still green |
