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
| 4 — Group/measure | done (v0) | — | §1–9 landed: bake (config/parser/validation, grouper, effective view+reducer, companions, domains+wiring), client (types+parser, fold, render integration), view+bake+verify PASS on 7.6M real facts. Live browser recolour is the one manual step left. |
| 4-fetch — Fetch locality | done | — | 4.1 SW cache + 4.2 prefetch landed (below); 4.3 pack container deferred (owner gate) |

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

### §2 — Fact grouper + perMark/perFact classification

**What landed.** A group-regime view's facts are grouped to a marks table (one row per geometry) with
the measures materialized at the default context, plus the derived channel classification. Pure bake
plumbing, DuckDB-only, no ClickHouse; nothing wired into the bake orchestration yet (§3).

- **New `IFactGrouper`** (Domain.Baking) + `FactGrouping(PerMarkChannels, PerFactChannels)`.
- **`DuckDbFactGrouper`** (Infrastructure): groups by the representative `(x, y)` — the engine's
  distinct-geometry key regardless of source, so **no adapter change and the raw quadkey need not be
  carried**. Emits `id` (mark key), `first(geometry/part_offsets)`, `first()` of each perMark channel,
  and each measure from its AST: flat aggregates (`sum/count/avg/wavg/min/max/share`, `where`→`FILTER`,
  share numerator `COALESCE(...,0)`/whole `nullif(...,0)`) in the main GROUP BY; `argmax/argmin` via a
  per-dimension sub-grouping (`arg_max(dim, inner)`) joined back on `(x, y)`.
- **Classification:** one pass — a channel is perFact iff `count(DISTINCT (x, y, ch)) > count(DISTINCT (x, y))`.
- **New `MarkKey`** (Infrastructure.Tiling): the shared `id`/`mk` derivation (real mark `p:x:y`,
  merged cell `g:gx:gy`) so the marks tile and the fact companion (§4) key marks identically.

**Acceptance evidence.** `dotnet test` 107 passed (+1). `FactGrouperTests` round-trips a synthetic
facts parquet → marks: 2 marks for 4 facts; `total_tests`/`wavg`/`share`/`argmax` values exact;
`region` classified perMark (carried via `first`), `operator`/`quarter`/`tests`/`download_mbps`
perFact (dropped from marks); geometry ring carried; ids distinct.

### §3 — Effective render view + AggregateReducer group-regime columns

**What landed.** The reducer can now tile a marks table: it carries the mark `id` and dict channels
alongside geometry/measures, with the right sub-pixel merge semantics. Still not wired into the bake
(nothing sets `GroupRegime` yet) — so the row regime is byte-for-byte unchanged and all Phase-0–3
tiles are identical.

- **`EffectiveView.For(authored, grouping)`** (Application): builds the internal marks view — `id`
  (identity/dict), each perMark channel (authored role/type), and every measure materialized
  (numeric → measure/f32; argmax → dimension/dict). `Measures = null` (it is the materialized table,
  not a group source), so `DictionaryEncodedChannels()` and the reducer treat it as an ordinary view.
- **`ReductionContext.GroupRegime`** (default false). **`AggregateReducer`**: when true, `ContentSql`
  also emits `id` (real → pass-through the marks id; merged → `MarkKey.MergedSql(gx,gy)`, matching the
  companion) and every dict channel (real → pass-through; merged → `mode()`); measures still average.
  When false the SQL is identical to before — the row-regime gate that keeps ookla-fixed/mobile-coverage
  tiles untouched.

**Acceptance evidence.** `dotnet test` 110 passed (+3). `EffectiveViewTests` pins the channel
materialization + dict-encoding set. `AggregateReducerTests` (synthetic marks parquet → DuckDB):
real marks pass `id`/`region`/`dominant_operator`/`total_tests` through; three sub-pixel marks in one
cell collapse to one row with a `g:` grid-key id, `mode()` of each dict, and the measure averaged.
Row-regime reducer/tiling tests unchanged.

### §4 — Fact companions + manifest fields

**What landed.** The reducer now writes a `z/x/y.facts.arrow` beside every render tile: the tile's
facts as partial-aggregate rows at grain, keyed by `mk` to the tile's mark ids. Still gated behind
`ctx.Companion` (unset until §5 wiring), so the row regime is untouched.

- **`MeasurePartials`** (Domain.Measures): the union of partial columns the measures need, with the
  deterministic names (`sum__ch`, `cnt`, `swp__ch__w`, `min__ch`, `max__ch`) that are the fourth
  cross-language contract; `share`/`argmax` reduce to their inner agg's partials.
- **`ReductionContext.Companion`** (`CompanionSpec`: facts path, grain channels, partials, canonical
  dict orders). **`AggregateReducer`**: `LoadTagged` now loads the facts too (same zreal formula, so a
  fact's merge decision matches its mark's); `CompanionSql` groups facts by `(mk, grain…)` where
  `mk = zreal ≤ z ? MarkKey.RealSql : MarkKey.MergedSql(gx,gy)` — the exact id the render tile carries.
  Companions ride the same active tiles, so each tile gets one.
- **Manifest** gains `FactChannels` (perMark/perFact split), `CompanionTiles`, `GrainChannels`
  (definitions only; populated in §5).

**Acceptance evidence.** `dotnet test` 114 passed (+4). `MeasurePartialsTests` pins the partial union.
`GroupBakeTests` (facts → grouper → effective view + companion → reducer, all DuckDB): real marks →
companion with 4 grain rows whose `mk`s are exactly the tile's mark ids, `Σ sum__tests`/`Σ swp`
correct; two marks that merge → companion keyed by the `g:` grid cell, partials folded across both.

### §5 — Two-staging domains + bake wiring (bake half complete)

**What landed.** The group regime is now wired into the bake end to end. This is the first commit that
sets `GroupRegime`/`Companion` in production — a view with a `measures` block now bakes to grouped
marks + companions; a view without one is unchanged (same path, same tiles).

- **`GroupRegimeArtifacts.Build`** (Application): scans the marks staging (numeric measures + perMark
  dims — default-context, stable scales) and the facts (perFact filter options + argmax colour
  domains), then assembles the manifest domains, the companion spec, and the canonical dict orders.
  **The crux:** an argmax measure colours over its *dimension's full domain* (a filter can make any
  value dominant), and the argmax measure and its dimension get the **same** canonical order — so the
  render tile's measure codes and the companion's dimension codes coincide, and the client folds
  argmax straight into the tile's colours with no remap.
- **`BakeViewUseCase`**: branches on `HasMeasures`. Group path: `GroupToMarks` → `GroupRegimeArtifacts`
  → reduce the marks with `GroupRegime`/`Companion`/render canonical orders; manifest carries the
  authored view plus `FactChannels`/`CompanionTiles`/`GrainChannels`. Grouper registered in DI.

**Acceptance evidence.** `dotnet test` 115 passed (+1). `GroupRegimeArtifactsTests` (facts → grouper →
domains, real scanner): only `apex` ever dominates, but `dominant_operator`'s colour domain is the full
`{apex, nova, zenith}`; `dominant_operator` (render) and `operator` (companion) resolve to the **same**
canonical order; `total_tests` numeric domain from marks, `quarter` temporal bounds from facts;
grain = `[operator, quarter]`. Row-regime bake path unchanged (all prior tests green). Note: the full
live bake of `mobile-dominance` (ClickHouse) is §9; this commit is unit-proven end to end without it.

### §6 — Client types + parser mirror + classification

**What landed.** The client-side data model for the group regime, plus the measure parser mirror. No
rendering change yet (the group-regime decode/fold is §7–8); the row regime is untouched — all helpers
short-circuit when `isGroupRegime` is false.

- **`measures.ts`** (parser half): the AST + `parseMeasure`, a faithful port of the C# grammar (same
  tokens, same errors). `measures.test.ts` pins it against the **shared** `measure-cases.json` — the
  same file the C# `MeasureParserTests` read, so the two parsers can't drift. Fold engine is §7.
- **`manifest.ts`:** `MeasureSpec`, `ViewConfig.measures?`, `FactChannels`, `Manifest.factChannels?`/
  `companionTiles?`/`grainChannels?`, and `factsUrl` (the `.facts.arrow` companion URL).
- **`channels.ts`:** `isGroupRegime`, `measureNames`, `measureChannelSpecs` (a measure → virtual
  channel: argmax → dict dimension, else f32 measure), `renderChannels` (the columns a tile actually
  carries — group regime = id + perMark + measures), group-aware `colorableChannels(manifest)`,
  measure-aware `colorChannelName`/`describeColorDomain`, and `splitFilters` (perFact → fold context,
  the rest → GPU predicate). Row-regime manifests split all-predicate — unchanged.

**Acceptance evidence.** `tsc -b` clean, `oxlint` clean, `vitest` 112 passed (+2, the parser fixture).
Cross-language parity: TS and C# now agree on every `measure-cases.json` AST and error.

### §7 — Fold engine (`foldTile`)

**What landed.** The client counterpart to the bake's partials: `foldTile(companion, measures, context,
markCount, markIndex, domains)` recomputes each measure over a tile's fact partials under the active
context, in one pass. Same finalization as the bake's default-context SQL — only the surviving fact set
differs, and partials are additive, so it's exact at every LOD.

- `InnerAgg` accumulates a numeric inner agg over groups (a mark; a (mark, dim) pair for argmax; the
  restricted/unrestricted halves of a share). `makeFolder` builds one folder per measure: flat aggs
  (own `where` → row filter), `share` (COALESCE(restricted,0)/nullif(whole,0)), `argmax/argmin` (per-dim
  inner, pick the extremal dim → its canonical code). A mark with no surviving fact is `NaN` / `ARGMAX_UNKNOWN`.
- `buildFoldContext` splits the perFact context into equality selections + temporal ranges.
- Date helpers moved to `dates.ts` (dependency-free) and re-exported from `channels.ts`, so `measures.ts`
  and `channels.ts` share them without an import cycle.

**Acceptance evidence.** `tsc -b`/`oxlint` clean, `vitest` 115 passed (+3). `foldTile` tests: default
context reproduces `sum`/`wavg`/`share`/`argmax` exactly; a `operator=apex` context folds to the apex
facts and turns a mark with none unknown (NaN / ARGMAX_UNKNOWN); a temporal range drops the out-of-range
bins. The values match the bake's `GroupBakeTests` companions by construction (same partials, same math).

### §8 — Rendering integration

**What landed.** The group regime renders end to end on the client. Split across two commits: §8a decode
(id + render channels + companion decode), §8b the fold wiring (hook + colour override + filter split +
inspect). Row regime untouched — every group path is gated on `isGroupRegime`/active context.

- **§8a:** `renderDecodeView` swaps the authored channels for the effective marks channels so a tile's
  measure/id columns decode with the right types; `fieldSelection` adds the mark `id`; `useTiles` passes
  the decode view to the worker. `decodeCompanion`/`loadCompanion` parse a `.facts.arrow` into
  `CompanionData` (mk, grain dims as strings, temporal as YYYY-MM-DD, partials as f32).
- **§8b:** `useMeasureFold(manifest, rendered, context)` decodes each tile's companion (cached, evicted
  with the tile) and folds it to per-mark measure columns, or `null` when there is no context (baked
  colours, zero cost). `tileDeckData` gains a folded `override` + a context key, so the colour value
  attribute comes from the fold and cached buffers are reused while scrubbing. `App` splits active
  filters via `splitFilters` — predicate (perMark) → the GPU `filterRange` exactly as before; context
  (perFact) → the fold, never a filterRange. Inspect reads folded measure values (numeric, or an argmax
  code decoded through its category domain) under context, baked otherwise.

**Acceptance evidence.** `tsc -b`/`oxlint` clean, `vitest` 120 passed (+3 over §8a's 117). Unit-verified:
group tile decode (§8a), `decodeCompanion` (§8a), `foldTile` (§7), `tileDeckData` override (numeric
passthrough, argmax code → unknown texel, context-keyed cache). The live browser scenario (dominant-operator
map recolouring as the quarter range / operator filter changes) is proven in §9 against a real bake.

### §9 — mobile-dominance view + fresh bake + verifier invariant

**What landed.** `views/mobile-dominance.json` (the VIEW_CONFIG §3 flagship) and the group-regime
verifier invariant. The view was baked **from the real 7.6M-fact `mobile-coverage` staging** (ClickHouse
was unavailable in this environment, so the identical extract was reused rather than re-run — same data,
same bake path as `BakeViewUseCase`'s group branch).

- **`views/mobile-dominance.json`:** loads + validates; `dominant_operator = argmax(operator, sum(tests))`,
  `apex_share = share(sum(tests)) where operator='apex'`, `avg_download = wavg(download_mbps, tests)`.
- **`VerifyFidelityUseCase`:** row regime unchanged (leaf sum == source rows). Group regime learns the
  honest invariant (GROUP-MEASURES §9): leaf **marks** == distinct marks (the grouped marks staging) and
  Σ leaf-**companion** rows == source rows. Added `ITileReader.Exists` for the companion probe.

**Acceptance evidence (real bake).**

| Criterion | Result |
|---|---|
| Bake `mobile-dominance` from 7.6M facts | 627,511 distinct quadkeys → **leafMarks = 627,511**; 69 tiles (48 leaves), maxZoom 3; companions written beside every tile |
| Classification | perFact = `operator, quarter, tests, download_mbps`; perMark = ∅ (geometry repeats per quarter×operator) — the marks pyramid is 627k, not 7.6M |
| Argmax colour domain | `dominant_operator` domain = `[apex, nimbus, orbit, pulse]` (the operator dimension's full domain, from the facts) |
| `verify` (all four views) | **PASS** — `mobile-dominance: leafRows=627,511 total=627,511 source=7,607,947` (leaf marks == distinct marks; companion facts == 7.6M source rows); geonames/mobile-coverage/ookla-fixed still PASS (row regime intact) |
| `dotnet test` | 115 passed (verifier + view changes compile; row-regime suite green) |

**Acceptance evidence (live browser, worktree servers).** Served the worktree tiles (:5174) + web
(:5173) and loaded `?view=mobile-dominance`:

- Manifest + companions load (200), **zero console errors**. Color-by lists the measures
  (`total_tests/avg_download/apex_share/dominant_operator`); `operator` filter shows `[apex,nimbus,orbit,pulse]`;
  legend = `dominant_operator` categorical over that domain; 161,132 marks resident in view.
- **The fold, run in-page over the real baked tile `3/1/0` (3,222 marks, 38,778 companion rows):** the
  `dominant_operator` distribution recolours completely per context — all facts → `{apex 2140, orbit 840,
  nimbus 185, pulse 57}`; `operator=apex` → `{apex 3088, unknown 134}` (apex's footprint); `operator=nimbus`
  → `{nimbus 2843, unknown 379}`. Marks with no surviving fact go unknown; the numeric `total_tests` mean
  recomputes (80 → 26 → 21). This is the archetypal filter-dependent computed colour, exact on real data.
- Not observable here: literal map pixels — the sandbox blocks MapLibre's external basemap tiles, so the
  map canvas never sizes/paints; the deck fold that drives the colours is proven above instead.

## Phase 4-fetch — Fetch locality (PHASE-4-fetch-locality.md)

Building the non-deferred half: 4.1 SW cache + 4.2 prefetch. 4.3 (pack container) stays deferred
behind owner sign-off, and the whole phase remains gated on real production latency data — this is
the additive, independently-landable infrastructure, not a data-model change.

### 4.1 — Persistent tile cache (Service Worker + Cache API)

**What landed.** `web/public/sw.js` (~65 lines, plain JS, no deps): immutable versioned tile paths
(`…/<viewId>/<version>/z/x/y[.facts].arrow`) are served cache-first from the Cache API; `latest.json`,
manifests, and the API pass through untouched. Cache name per `(viewId, version)`; on manifest
activation the client posts `{viewId, version}` and the SW drops the view's other-version caches
(version rotation = the GC). `cache.put` is wrapped for `QuotaExceededError` (evict oldest tile cache,
retry once, else network-only). `web/src/lib/swClient.ts` registers it **production-only** (`import.meta.env.PROD`)
and posts the activation message from `useViewData` after `loadManifest`. `fetchArrowTable` is untouched —
the SW is transparent.

**Acceptance evidence.** `node --check public/sw.js` OK; `tsc -b`/`oxlint` clean; `vitest` 120 passed;
`npm run build` succeeds and ships `dist/sw.js`. (Live offline-reload / version-flip is a production-bundle
scenario; dev never registers the SW by design.)

### 4.2 — Predictive prefetch

**What landed.** During idle, once the tile selection has been stable ~300ms and every selected tile is
resident, the likely-next tiles are warmed via `requestIdleCallback` (fallback `setTimeout`).

- **`prefetchCandidates(manifest, selKeys, cap=12)`** (tiling.ts, pure): parents (zoom-out), the one-tile
  pan ring, then children (zoom-in); only tiles the manifest baked, never the selection, capped.
- **`useTiles`:** a debounced idle effect ensures the candidates through the normal cache path, guarded
  by *all selected resident* (never race a demand load) and `bytesResident() < 75% TILE_BUDGET_BYTES`
  (never evict for a guess). The selection effect keeps the candidates in `abortStale`'s survive-set, so a
  resolving prefetch's snapshot commit doesn't cancel its siblings; a camera move drops them from the set
  and cancels them — no new cancellation mechanism. `TileCache` gains `bytesResident()`; `TILE_BUDGET_BYTES`
  is exported.

**Acceptance evidence.** `tsc -b`/`oxlint` clean; `vitest` 123 passed (+3). `prefetchCandidates` tests
pin the candidate set (parents/ring/children, baked-only, never the selection, cap honoured).

## Post-build verification (2026-07-12, owner-requested)

Independent session, ClickHouse **available** (docker up, all three source tables at expected counts).
Full rebake of all four views **from source** (not staging reuse): geonames 13,447,008 rows / 197
tiles; mobile-coverage 7,607,947 / 69; mobile-dominance 7,607,947 facts → 627,511 marks / 69 tiles +
companions; ookla-fixed 6,655,986 / 3,596. `verify` PASS on all four (leafRows == source for the row
regime; leaf marks == distinct marks for the group regime). Suites green at the merge commit:
`dotnet test` 115, `tsc -b`/`oxlint` clean, `vitest` 123. All served manifests `tileFormat: 2`;
geonames residency 128,212 cells / 4 tiles at the baseline camera — identical to the build's
evidence. Old tile versions (7 dirs, ~6.4GB) deleted; each view now has exactly one baked version.

## Owner-verification fixes (2026-07-12, second pass)

Live inspection by the owner surfaced four issues in the group regime's first cut; all fixed, all
verified live against the real bake:

1. **Silent default context (UX + perf).** `useViewData` defaulted every *polygon* view to one clean
   slice (first dimension value + latest temporal) — right for row-regime overdraw, wrong for the
   group regime: mobile-dominance loaded with `operator=apex, quarter=latest` silently active, so the
   fold ran from first paint and the filter UI read as inverted. Group-regime views now start
   unfiltered — baked colours, zero fold work; the slice heuristic stays for row-regime polygons.
2. **Poisoned override cache (the "one-behind" filters).** `App` keyed fold-derived GPU buffers by the
   *live* selection while `folded` still held the previous context's arrays — stale colours were
   cached under the new key and served forever. `useMeasureFold` now returns `{byTile, contextSig}`
   and buffers key on the sig that produced the fold, so an in-flight fold can never mislabel buffers.
3. **mobile-coverage rendered blank.** Row-regime aggregate tiles carry no dimension/temporal columns
   (the reducer averages every slice per cell), but the view still offered those filters and the slice
   default selected one — every mark read `MISSING_CODE` and was GPU-discarded. New
   `carriedFilterableChannels`/`predicateChannels` (channels.ts): controls render only for channels
   the tiles can answer; filter slots build only from predicate-capable ones. mobile-coverage now
   renders the all-facts average map with no dead controls. (Its hard data edge at lon 0–45°E is the
   dataset's real extent — see scripts/seed-mobile-coverage.ps1 — not a rendering fault.)
4. **Companion pipeline rebuilt for scale.** Companions were 459MB of the 603MB view; a low-zoom
   companion was fetched, Arrow-parsed, and string-joined **on the main thread**, serially, and
   re-folded from scratch on every pan. Now:
   - **Bake:** companions carry `mki` (Int32 — the fact's mark row index within its render tile, via
     a materialized `row_number()` content join) and drop the `mk` strings; render-tile row order is
     deterministic (`ORDER BY id` within tile). Root companion 29.3MB → **18.4MB**; all companions
     459MB → **232MB** (view total 603MB → 376MB). Render tiles byte-equivalent.
   - **Client:** companion fetch+decode moved to the tile worker pool (typed columns — dict codes,
     day numbers, f32 partials — transferred zero-copy); loads parallelized; fold results cached per
     `(version, tile, contextSig)` so pan/zoom under a held context re-folds nothing; `foldTile`'s
     hot loop is typed-array compares/adds joined by `mki` — no strings, no hashing, no per-row
     allocation. The `tileDeckData` per-tile cache is capped (16, oldest-first) so context scrubs
     can't pin unbounded per-vertex buffers.

   **Measured live** (root tile as rendered at world zoom: 60,741 marks, 1,225,878 companion rows):
   companion fetch+decode **101ms** (on the worker in-app), fold **33–43ms per context** (140ms first
   call, JIT), **zero main-thread long tasks** through a full `operator=apex` apply in the UI. Fold
   distributions on the live tile: all → {apex 33,312, orbit 12,587, nimbus 11,864, pulse 2,978};
   `operator=apex` → {apex 59,142, unknown 1,599}; `operator=nimbus` → {nimbus 57,111, unknown 3,630};
   2024-Q1-only → 20,383 unknown, mean total_tests 683 → 92 — filter-dependent computed colour, exact.

Suites after the pass: `dotnet test` 115, `vitest` 125 (+2), `tsc -b`/`oxlint` clean; mobile-dominance
re-baked (`v20260712T081835Z`) and `verify` PASS on all four views (the group invariant is
schema-agnostic, so `mki` needed no verifier change). Also fixed in passing: `deckData.ts` contained a
literal NUL byte (git treated the file as binary) — the cache-key separator is now the source escape
sequence for U+0000. GROUP-MEASURES.md §4/§7 updated to the `mki` contract.

## Status summary

Group/measure v0 (§1–9) and fetch-locality 4.1–4.2 are complete: `dotnet test` 115, `vitest` 123, both
green; `verify` PASS on all four views (incl. the group-regime `mobile-dominance` on 7.6M real facts);
the live fold recolour proven in-browser. Deferred per the plan: fetch-locality 4.3 (pack container,
owner sign-off) and PHASE-5 (deferred frontier). The group model's own v0 "Out" column (GPU fold,
keyed wkt/geohash/h3, point group marks, server DuckDB fold, quadtree/raw group regime) also stays
designed-not-built, as scoped.
