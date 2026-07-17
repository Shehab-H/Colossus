# Requirement 4 — Remote fold routing — Build report

**Status: BUILT + VERIFIED 2026-07-17.** R4 makes the fold a *routed* operation: the bake planner prices each
group-regime view's per-interaction cost and records `foldRoute.execution` (`client` | `remote`) in the
manifest. An over-budget view executes its fold on the server (DuckDB over the baked facts Parquet) behind
the **same** `fold(measures, context) → columns` seam, shipping folded columns instead of companion planes.
This is the engine's first runtime compute component; RULES R7's static tile serve is untouched — the fold
endpoint is additive, tiles remain immutable static files (REQUIREMENTS.md R4, owner sign-off 2026-07-16).

Frozen and unchanged: the seam signature, the measure grammar (VIEW_CONFIG §4), `(version, tileKey)`
identity, no source-DB contact at runtime, the static tile serve, and the fold-result cache keys.

---

## Baseline

Measured **before any engine change**, on the then-latest `mobile-dominance` bake, so the After numbers
compare like-for-like (same machine, same method, same viewport, same contexts).

### Environment

| | |
|---|---|
| Commit | `fef883f` (`claude/r4-remote-fold` branched from this; no engine change yet) |
| Machine / OS | Windows 11 Pro (10.0.26200), win-x64 |
| Runtimes | Node v22.16.0, .NET 10.0.301 |
| View | `mobile-dominance` — baseline version `v20260716T101617Z` |
| Format | `slab/sparse` (CSR), grain axes `operator` (categorical) × `quarter` (ordered); 24.26M source facts → 2.0M marks |
| Color measure | `dominant_operator` = `argmax(operator, sum(tests))` (planes `sum__tests` + `@idx`) |

### Method

The harness `web/scripts/bench-fold-route.ts` measures the **real client fold path** — it ranges the
color-measure planes out of `facts.pack` and runs the same `decodeSlab` / `foldSlab` the tile worker runs.
It takes each fold-context's best of 3 timed runs (after two warm-up passes) and reports p50/p95 across the
context set. Exact command:

```
cd web
npx vite-node scripts/bench-fold-route.ts -- mobile-dominance
```

### Fixed benchmark viewport (recorded)

A dense, contiguous **z5 (leaf) block over Europe** — 12 tiles, 583,511 marks. z5 is leaf zoom for this bake
(all 255 z5 tiles are leaves). The tile list is pinned in the harness (`VIEWPORT`) and here:

```
5/15/18  5/15/19  5/15/20
5/16/18  5/16/19  5/16/20
5/17/18  5/17/19  5/17/20
5/18/18  5/18/19  5/18/20
```

Every re-bake below reproduced this viewport identically (12/12 tiles, 583,511 marks), so before/after
compare on the same marks.

### Contexts

59 varied fold contexts: every `operator` equality selection, a grid of `quarter` ranges (cumulative-from-
start + sliding windows across the temporal span), and their cross product — the same construction the R1
harness uses. All 59 are distinct, so each is a genuine cache miss (a real interaction) on both routes.

### Measured baseline, as taken (⚠ invalid as a comparator — see below)

| Metric | Value |
|---|---|
| Transfer bytes / interaction — cold (viewport's color-measure planes, plane-split, R5) | 7,260,314 B (≈6.92 MB) |
| Transfer bytes / interaction — warm (filter change, planes resident) | 0 B |
| Client fold ms per filter change — p50 / p95 | 2.19 ms / 77.01 ms |

**These client-fold numbers do not mean what they appear to.** Building R4's parity gate surfaced a
pre-existing R1 defect: the slab's ordered-axis domain was recorded as raw day numbers (`'19723'`) because a
ClickHouse `Date` extracts as `USMALLINT`, while contexts arrive as ISO (`'2025-01-01'`). Every date-range
fold therefore resolved `lo > hi` → *impossible* → returned instantly with **every mark unknown** (the map
blanked). 48 of the 59 contexts carry a date range, so the p50 of 2.19 ms was largely measuring a
short-circuit, not a fold. The fix is described under *Changes* below.

### Corrected baseline (same viewport, same 59 contexts, same method, on the fixed bake)

The honest local-route comparator, re-measured on `v20260717T100349Z` once ranges actually fold:

| Metric | Value |
|---|---|
| Transfer bytes / interaction — cold | **7,260,314 B** (≈6.92 MB) |
| Transfer bytes / interaction — warm (filter change) | **0 B** |
| Client fold ms per filter change — p50 / p95 | **51.96 ms / 136.35 ms** |
| End-to-end latency per filter change — p50 / p95 | **== client fold ms** (warm: no fetch, no network) |

**Reading these.** On the local route R5's plane split fetches the color measure's planes for a viewport
**once** (7.26 MB across these 12 tiles); every later filter change with the same measure is fold-only over
resident planes (0 transfer), so end-to-end latency per filter change *is* the client fold time.

---

## Changes

1. **Facts retained per version.** A group-regime reduction writes `<version>/facts.parquet`
   (`manifest.factsParquet`) — the server fold's input. It is a *purpose-built* artifact, not a copy of the
   staging extract: it carries `(x, y, zreal, grain…, measure channels…)` and **drops `geometry`**, which
   only ever existed to derive `zreal`. Baking `zreal` in turns each fold from "re-read and re-measure every
   polygon" into a column scan, and ordering by `x` lets a viewport's bbox prune row groups. Measured effect
   on this viewport: server fold p50 **9,810 ms → 4,071 ms**. It is read with DuckDB over the baked artifact
   — the source DB is never contacted at runtime (RULES R5).
2. **Planner pricing.** The bake prices each group view's per-interaction cost from the **measured** plane
   bytes the reduction just wrote (worst leaf tile + a dense-screenful estimate) against a configurable
   budget, and records `manifest.foldRoute`. Config: `FoldRouting:BudgetBytes` in
   `src/Colossus.Bake/appsettings.json`, **default 32 MB**; `COLOSSUS_FOLD_FORCE_REMOTE=1` forces remote at
   bake, `?fold=remote` forces the client onto the remote route for one session.
3. **Server executor.** `POST /api/views/{id}/fold` takes the compiled context (equality selections + date
   ranges) + tile keys, and returns per-tile, mki-keyed Arrow columns (Float32 numeric; UInt16 canonical
   codes for argmax), with the per-tile row directory in the Arrow schema metadata so there is no per-row
   tile column on the wire. Marks with no surviving facts come back NaN / unknown, exactly like the client.
4. **Client executor.** `web/src/lib/remoteFold.ts` behind the same seam; identical output types, identical
   fold-result cache keys, `(version, tileKey)` untouched, batched one request per viewport. The remote
   route fetches **no** companion planes.
5. **R1 fix (prerequisite, owner-approved in-scope).** The ordered axis now records its domain in canonical
   ISO — the form `tests/fixtures/slab-cases.json` and the client's range compare already require —
   normalising whatever the adapter delivered (DATE / integer day count / epoch millis) via the same split
   `web/src/lib/dates.ts` uses. See SLAB-FORMAT.md §1.

### Measured route pricing (both reference views)

| View | Worst tile / interaction (measured) | Budget | Route |
|---|---|---|---|
| `mobile-dominance` | 1,825,714 B | 32,000,000 B | `client` |
| `mobile-coverage` | 8,128,096 B | 32,000,000 B | `client` |

The budget encodes a limit the **client** actually hits. After R5's plane split the reference views' worst
leaf interaction measures ~1.8 / ~8.1 MB, which the browser folds in tens of ms — so both stay client, and
REQUIREMENTS' design scenario (a dense leaf costing *tens of MB* per interaction) is what prices remote.
This was calibrated from measurement: an earlier 8 MB default put `mobile-coverage` (8.13 MB) 1.6% over the
line and would have moved a shipped view from a 52 ms client fold to a ~4 s server fold — a regression, and
evidence that 8 MB was not a real client limit. The planner demonstrably routes remote when a view *is* over
budget (at an 8 MB budget `mobile-coverage` priced `remote`).

---

## After — remote route

Same machine, same fixed viewport, same 59 contexts, same harness. **The remote numbers were taken against
the production artifact** — `dotnet publish -c Release -r win-x64 --self-contained`, run with
`ASPNETCORE_ENVIRONMENT=Production` and `TilesRoot=tiles` relative to the content root (a Debug dev server
measured ~2.4× slower and would not have represented a deploy). Both reference views price `client`, so the
benchmark **forced the remote route** (the harness posts to the endpoint directly; the browser equivalent is
`?fold=remote`).

```
cd web
npx vite-node scripts/bench-fold-route.ts -- mobile-dominance --remote http://localhost:5199 --parity
```

### Comparison — before (local route) vs after (remote route)

| Metric | Before — local route | After — remote route | Δ |
|---|---|---|---|
| Transfer bytes / interaction (fixed viewport) | 7,260,314 B cold, then **0 B** per filter change | **1,167,592 B** every filter change | −84% on the cold interaction; +1.17 MB on every warm one |
| End-to-end latency / filter change — p50 | **51.96 ms** | **4,150.34 ms** (localhost) | +4,098 ms (≈80×) |
| End-to-end latency / filter change — p95 | **136.35 ms** | **4,943.46 ms** (localhost) | +4,807 ms (≈36×) |
| Fold compute ms — p50 / p95 | **client fold** 51.96 / 136.35 ms | **server fold** 4,071 / 4,871 ms | reported separately, see below |
| Response bytes vs `marks × measures × 4 B` | n/a (ships facts, not columns) | **1,167,592 B** actual vs **2,334,044 B** formula | 0.50× formula — explained below |

**Fold compute, reported separately (not a like-for-like pair).** The client fold (51.96 / 136.35 ms) folds
**pre-reduced companion planes** — the bake already collapsed 24.26M facts into grain cells. The server fold
(4,071 / 4,871 ms) rebuilds those cells from 2.4M viewport facts on every request. They are different
amounts of work for the same answer; the number that matters to a user is the end-to-end row.

**Response-bytes sanity check.** The `marks × measures × 4 B` formula assumes an f32 column per measure. The
benchmark's color measure is `argmax`, which ships **UInt16 canonical codes** (2 B), so the response is half
the formula and lands within 570 B of the exact prediction: 583,511 marks × 2 B = 1,167,022 B, actual
1,167,592 B (the remainder is Arrow framing + the per-tile directory). The formula holds exactly when the
measures are numeric: the prod-path smoke folded `dominant_operator` (u16) + `total_tests` (f32) over one
tile and returned 744,648 B vs 124,020 × (2+4) = 744,120 B predicted.

**Latency was measured over localhost.** Client and server were the same machine, so these figures contain
**no network transit** — the remote route's ~4.15 s p50 is essentially all server fold time, and the local
route's transfer advantage is understated because moving 1.17 MB cost ≈0 here. **No WAN numbers are
reported and none should be inferred from these**; the transfer-bytes row is the only quantity that
transfers to a real network unchanged. A WAN comparison was **not measured** — there is no non-localhost
deployment in this environment to measure against.

### What this says about the route

For the reference views the local route wins decisively on latency (52 ms vs 4,150 ms), which is exactly why
the planner prices them `client` and why R4's entry criteria were about *over-budget* views. The remote
route's value is the axis this table shows in its favour: per-interaction transfer is **bounded by the
answer** (`marks × measures × small`, 1.17 MB here, independent of fact count) rather than by the fact
volume behind it. A view whose leaf companion is tens of MB per interaction cannot be folded in a browser at
all; it can be folded on the server in seconds. That is the trade R4 buys, and the planner is what decides
when it is worth paying.

---

## Verification

| Gate | Result |
|---|---|
| Every `tests/fixtures/measure-cases.json` case through the server fold | **PASS** — `ServerFoldTests` compiles every `parse` case to fold SQL and rejects every `errors` case via the shared parser |
| Server fold values vs pinned fixtures | **PASS** — `ServerFoldTests.ServerFold_MatchesEveryFixtureContext` folds `tests/fixtures/slab-cases.json` through the REAL fold SQL (`FoldSql.Tail`) across all 6 contexts × 6 measures (sum, wavg, count, max, argmax, share), matching the values `slab.test.ts` pins in TS |
| Byte-compare remote vs local, ≥50 contexts, full benchmark viewport | **PASS** — **708/708** (59 contexts × 12 tiles), 0 failures, against the production binary |
| `dotnet test` | **PASS** — 122/122 |
| `cd web && npx tsc -b && npm run lint && npm run test` | **PASS** — tsc clean, oxlint clean, 137/137 |
| `dotnet run --project src/Colossus.Bake -- verify` (fresh bakes) | **PASS** — see below |
| Browser, route FORCED remote | **PASS** — `?fold=remote` renders `mobile-dominance`, `operator=apex` recolors via `POST /fold → 200`, **zero** `facts.pack` fetches, no console errors; `?fold=client` renders identically |
| Prod-path smoke | **PASS** — self-contained `win-x64` publish, `ASPNETCORE_ENVIRONMENT=Production`, `TilesRoot=tiles` relative → `HTTP 200`, `application/vnd.apache.arrow.stream`, `X-Fold-Ms: 451`. `duckdb.dll` ships in the self-contained output |

**NaN comparison (parity definition).** Two folded columns are equal iff they have equal length and, at
every index, either both values are NaN (`Number.isNaN`-equivalent — NaN is the empty-mark sentinel, and
`NaN !== NaN` under `===`) or the values are strictly equal (`===`). For argmax columns that is an exact
integer compare of the u16 canonical codes, `65535` = unknown; for numeric columns it is exact f32 equality,
not a tolerance.

**Why remote and local agree bit-for-bit.** The server does not fold the raw facts. It reproduces the
companion's grain cells — the same `COALESCE(sum(ch),0)::FLOAT` partials the bake writes — and folds
*those*, accumulating in DOUBLE exactly as the client's `InnerAgg` accumulates the same f32 planes in
Float64. Both routes therefore share every intermediate rounding. Folding raw facts directly would have
summed in a different order and diverged in the last ulp.
