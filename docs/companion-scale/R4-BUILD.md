# Requirement 4 — Remote fold routing — Build report

**Status: IN PROGRESS.** R4 makes the fold a *routed* operation: the bake planner prices each group-regime
view's per-interaction cost and records `foldExecution: client | remote` in the manifest. An over-budget
view executes its fold on the server (DuckDB over the baked facts Parquet) behind the **same**
`fold(measures, context) → columns` seam, shipping folded columns (~`marks × measures × 4 B`) instead of
companion planes. This is the engine's first runtime compute component; RULES R7's static tile serve is
untouched — the fold endpoint is additive, tiles remain immutable static files (REQUIREMENTS.md R4, owner
sign-off 2026-07-16).

Semantics are frozen: the seam signature, the measure grammar (VIEW_CONFIG §4), `(version, tileKey)`
identity, no source-DB contact at runtime, static tile serve, and the fold-result cache keys.

---

## Baseline

Measured **before any engine change**, on the latest `mobile-dominance` bake, so the After numbers
compare like-for-like (same machine, same method, same viewport, same contexts).

### Environment

| | |
|---|---|
| Commit | `fef883f` (`claude/r4-remote-fold` branched from this; no engine change yet) |
| Machine / OS | Windows 11 Pro (10.0.26200), win-x64 |
| Runtimes | Node v22.16.0, .NET 10.0.301 |
| View | `mobile-dominance` — version `v20260716T101617Z` |
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

A dense, contiguous **z5 (leaf) block over Europe** — 12 tiles, 583,511 marks. z5 is leaf zoom for this
bake (`maxZoom = 5`; all 255 tiles at z5 are leaves). The tile list is pinned in the harness (`VIEWPORT`)
and here:

```
5/15/18  5/15/19  5/15/20
5/16/18  5/16/19  5/16/20
5/17/18  5/17/19  5/17/20
5/18/18  5/18/19  5/18/20
```

### Contexts

59 varied fold contexts: every `operator` equality selection, a grid of `quarter` ranges (cumulative-from-
start + sliding windows across the temporal span), and their cross product — the same construction the R1
harness uses, so before/after and local/remote runs see the identical set. All 59 are distinct, so each is a
genuine cache miss (a real interaction) on both routes.

### Measured baseline (local route)

| Metric | Value |
|---|---|
| Transfer bytes / interaction — cold (fetch the viewport's color-measure planes, plane-split, R5) | **7,260,314 B** (≈6.92 MB) |
| Transfer bytes / interaction — warm (filter change, planes already resident) | **0 B** |
| Client fold ms per filter change — p50 | **2.19 ms** |
| Client fold ms per filter change — p95 | **77.01 ms** |
| End-to-end latency per filter change — p50 / p95 | **== client fold ms** (warm: no fetch, no network) |

**Reading these numbers.** On the local route, R5 plane-splitting fetches the color measure's planes for a
viewport **once** (7.26 MB across these 12 tiles); every subsequent filter change with the same measure is
**fold-only over resident planes** (0 transfer bytes), so end-to-end latency per filter change equals the
client fold time. The p95 fold (77 ms) is dominated by the two densest tiles (`5/16/19`, `5/17/19` — ~112k
and ~124k marks each). These are the numbers R4's remote route is measured against in the After section.

<!-- After section appended once the remote route is built + verified. -->
