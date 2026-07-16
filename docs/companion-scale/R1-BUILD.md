# R1 build evidence — slab companion format (+ R5 plane-split co-build)

[REQUIREMENTS.md](REQUIREMENTS.md) Requirement 1 (slab companion format) plus the two Requirement 5
bullets the sheet says to co-build with it: per-plane byte ranges and internal-companion compression.
Slots under the R2 leaf pack (`CompanionPackWriter` wraps whatever leaf bytes the companion format
produces). Fold semantics, the measure grammar, and `tests/fixtures/measure-cases.json` are frozen —
this changes the *physical* companion, never what a fold computes.

## Baseline (measured before any code change)

**Environment.** commit `c06e3ca` (branch point of `claude/r1-slab-format`), .NET SDK `10.0.301`,
Node `v22.16.0`, Intel Core (Alder Lake, model 151), 12 logical cores, Windows 11. ClickHouse
`24.8` in Docker, `colossus.mobile_coverage` = 24,257,354 rows. `(quadkey, operator, quarter)` is
unique in the source, so grain rows == facts. Axis cardinalities from the source: **operator = 4**
(categorical), **quarter = 8** (ordered) → **32 cells**. Global occupancy
`facts / (marks × cells) = 24,257,354 / (2,008,081 × 32) =` **0.378 (37.8 %)** — inside the
"32–42 %" reality-check band, so the reference views are sparse-CSR territory.

**Commands (exact).**

```
# fresh baseline bake of both reference views (row-form companion, unchanged code @ c06e3ca)
docker compose -f docker/docker-compose.yml up -d
for v in mobile-coverage mobile-dominance; do dotnet run --project src/Colossus.Bake -- $v; done

# client cost + bytes at rest, real decodeCompanion + foldTile over the real facts.pack
cd web && npx vite-node scripts/bench-companion.ts -- mobile-coverage mobile-dominance

# internal-level companion bytes (uncompressed per-tile files)
find tiles/<view>/<version> -name '*.facts.arrow' -printf '%s\n' | awk '{s+=$1} END{print s}'
```

The harness ([web/scripts/bench-companion.ts](../../web/scripts/bench-companion.ts)) reads
`latest.json → manifest.json`, ranges each leaf block out of `facts.pack` exactly as the worker does,
and calls the **same** `decodeCompanion` + `foldTile` the app runs. The worst leaf tile is the one
with the largest packed block; fold p50/p95 are the best-of-3 per context over 60 varied contexts
(operator equality × synthesized quarter ranges). The identical script measures the "after" slab.

Baked baselines: mobile-coverage `v20260716T085407Z`, mobile-dominance `v20260716T085909Z`
(both: 380 tiles, 255 leaves, 2,008,081 marks, maxZoom 7). Worst tile = `5/17/19`, 124,020 marks.

| Metric (per view) | mobile-coverage | mobile-dominance |
|---|--:|--:|
| Leaf companion bytes — raw (Arrow, uncompressed) | 752,415,544 | 364,162,248 |
| Leaf companion bytes — `facts.pack` (gzip blocks) | 384,928,260 | 127,036,233 |
| Internal-level companion bytes (125 uncompressed files) | 748,511,720 | 362,227,096 |
| Worst leaf tile — raw block | 52,151,880 | 25,235,128 |
| Worst leaf tile — packed block | 26,500,522 | 8,680,090 |
| Worst-tile whole fetch (what the app fetches today) | 26,500,522 | 8,680,090 |
| Worst-tile active-measure fetch (row form = whole block) | 26,500,522 | 8,680,090 |
| Decode/prep ms (gunzip + Arrow parse + decode), median | 221.9 | 133.08 |
| Peak decoded bytes (typed arrays the fold reads) | 55,514,944 | 28,598,624 |
| Fold ms p50 / p95 (60 contexts) | 39.05 / 106.03 | 36.03 / 99.99 |
| Bake wall time | 302,985 ms | 202,733 ms |

Cross-checks against the 2026-07-13 reality check (which was on the older maxZoom-5 bake): leaf pack
367.1 / 121.1 MB ≈ measured 384.9 / 127.0 MB; internal 713.8 / 345.4 MB == measured 748.5 / 362.2 MB
(byte-identical framing, MB vs MiB rounding); worst-tile raw 50.5 / 25.3 MB ≈ measured 52.2 / 25.2 MB.
The active-measure (color-measure) recolor is `avg_download` for coverage (partials
`swp__download_mbps__tests`, `sum__tests`) and `dominant_operator` for dominance (partial
`sum__tests`); the row form has no plane split, so its active-measure fetch is the whole block.

## After (slab, measured on the same machine + harness as the baseline)

**Environment.** Same machine as the Baseline (Alder Lake, 12 logical cores, Windows 11, .NET
`10.0.301`, Node `v22.16.0`, ClickHouse `24.8`). Slab bakes: mobile-coverage `v20260716T101222Z`,
mobile-dominance `v20260716T101617Z` — both `slab/sparse` (measured occupancy 37.8 % < 0.5 gate → CSR),
380 tiles / 255 leaves / 2,008,081 marks / maxZoom 7, worst leaf tile `5/17/19` (124,020 marks) — the
same worst tile as the baseline. Fidelity verifier: **PASS** all views (slab witness `Σ cnt == 24,257,354
== source rows`; row-regime geonames/ookla byte-for-byte unchanged). All numbers below are from the
**identical** commands the Baseline used, re-run against these bakes:

```
for v in mobile-coverage mobile-dominance; do dotnet run --project src/Colossus.Bake -- $v; done  # wall-timed
cd web && npx vite-node scripts/bench-companion.ts -- mobile-coverage mobile-dominance
```

### mobile-coverage

| Metric | Before (row) | After (slab/sparse) | Δ |
|---|--:|--:|:--|
| Leaf companion bytes — raw | 752,415,544 | 615,415,616 | ×0.82 / **−18.2 %** |
| Leaf companion bytes — packed (`facts.pack`) | 384,928,260 | 372,473,506 | ×0.97 / −3.2 % |
| Internal companion bytes — raw | 748,511,720 | 610,056,576 | ×0.82 / −18.5 % |
| Internal companion bytes — compressed | 748,511,720 (uncompressed files) | 389,551,067 (packed) | **1.92× / −48.0 %** |
| Worst leaf tile — raw block | 52,151,880 | 42,556,416 | −18.4 % |
| Worst leaf tile — packed block | 26,500,522 | 25,645,662 | −3.2 % |
| Worst-tile whole fetch | 26,500,522 | 25,645,662 | −3.2 % |
| **Worst-tile active-measure fetch (plane-split)** | 26,500,522 | 7,828,921 (`avg_download` planes) | **3.39× / −70.5 %** |
| Decode/prep ms (median) | 221.9 | 132.08 | −40.5 % |
| Peak decoded bytes | 55,514,944 | 42,552,834 | **−23.4 %** |
| Fold ms p50 / p95 (60 contexts) | 39.05 / 106.03 | 2.56 / 36.30 | **15.3× / 2.9× faster** |
| Bake wall time | 302,985 ms | 222,902 ms | −26.4 % (cache-sensitive) |

### mobile-dominance

| Metric | Before (row) | After (slab/sparse) | Δ |
|---|--:|--:|:--|
| Leaf companion bytes — raw | 364,162,248 | 226,797,160 | ×0.62 / **−37.7 %** |
| Leaf companion bytes — packed (`facts.pack`) | 127,036,233 | 114,538,906 | ×0.90 / −9.8 % |
| Internal companion bytes — raw | 362,227,096 | 223,592,952 | ×0.62 / −38.3 % |
| Internal companion bytes — compressed | 362,227,096 (uncompressed files) | 118,073,227 (packed) | **3.07× / −67.4 %** |
| Worst leaf tile — raw block | 25,235,128 | 15,638,232 | −38.0 % |
| Worst leaf tile — packed block | 8,680,090 | 7,828,921 | −9.8 % |
| Worst-tile whole fetch | 8,680,090 | 7,828,921 | −9.8 % |
| **Worst-tile active-measure fetch (plane-split)** | 8,680,090 | 1,648,352 (`dominant_operator` planes) | **5.27× / −81.0 %** |
| Decode/prep ms (median) | 133.08 | 42.93 | −67.7 % |
| Peak decoded bytes | 28,598,624 | 15,636,514 | **−45.3 %** |
| Fold ms p50 / p95 (60 contexts) | 36.03 / 99.99 | 1.71 / 42.72 | **21.1× / 2.3× faster** |
| Bake wall time | 202,733 ms | 154,250 ms | −23.9 % (cache-sensitive) |

### Against the 2026-07-13 calibrated expectations

Every demanded win is met or beaten, and no wire number is chased past what the data supports:

- **Plane-split interaction fetch ≥3× on the worst tile** — **3.39×** (coverage) / **5.27×** (dominance).
  The active-measure recolor fetches only its partial planes (+ CSR structure), not the whole block.
- **Internal compression ~2–2.7×** — **1.92×** (coverage) / **3.07×** (dominance). Internal companions are
  now packed into `facts.pack` instead of served as uncompressed per-tile files (R5 internal compression),
  which also unified the fetch path.
- **Raw decoded bytes −18 %/−38 %** — measured **−23.4 %** / **−45.3 %** peak decoded bytes (the `mki`
  column is eliminated; CSR offsets + narrow `u16` cellIds replace it).
- **O(1) indexed fold with a *measured* fold-time win** — sparse compiles the context to one `Uint8[cells]`
  per-cell predicate then scans `nnz` with no per-row key decode: p50 **15.3×** (coverage) / **21.1×**
  (dominance) faster, p95 2–3× faster. (The dense cumulative layout's strict two-slice O(1) range fold is
  exercised by the shared fixture; it does not trigger on these views because their 37.8 % occupancy sits
  below the 0.5 dense gate.)
- **Leaf gz wire near-flat (−3 %/−10 %)** — measured **−3.2 %** (coverage) / **−9.8 %** (dominance), exactly
  the expected band; the wire win was never the point of R1.

**Caveats (measured, not estimated).** Bake wall time is sensitive to ClickHouse cache warmth — the after
run had a warm DB (up ~1 h) vs the baseline's cold start, so the −24 %/−26 % is not claimed as a slab bake
speedup, only recorded. The dense O(1) fold path is not measured on a real view (no registered view clears
the 0.5 occupancy gate); its correctness is pinned by [slab-cases.json](../../tests/fixtures/slab-cases.json)
across both C# and TS.

### Plane-split realized in the live client (browser-measured)

The plane-split rows above measure the *format's* capability; the running client now exercises it. The map
folds only the active colour measure ([`useMeasureFold`](../../web/src/hooks/useMeasureFold.ts) is handed
`mapMeasures = [renderChannel]`), so each on-screen tile fetches just that measure's planes — not the whole
companion — with planes cached under the tile key (SLAB-FORMAT §5). Measured live against the running app
(mobile-dominance, colour `dominant_operator` → planes `[sum__tests, @idx]`), per-tile scoped wire bytes vs
the whole-tile region:

| on-screen tile | scoped fetch | whole tile | Δ |
|---|--:|--:|:--|
| 1/0/0 | 185,155 | 730,711 | **3.95×** |
| 1/1/0 | 222,967 | 807,435 | **3.62×** |
| 1/0/1 | 421,400 | 1,565,465 | **3.72×** |
| 1/1/1 | 1,034,808 | 3,678,518 | **3.55×** |

Switching the colour measure fetches only the **delta** plane: dominant_operator → avg_download added just
`[swp__download_mbps__tests]` (the resident `sum__tests` + `@idx` were not refetched). A measure that needs
every partial (avg_download = `swp` + `sum`) fetches the whole tile — the win is per active measure, largest
for the single-plane measures (dominant_operator / total_tests / apex_share) that are the common colouring.
The tooltip still shows every inspect channel: it fetches the remaining inspect planes for the one clicked
tile on demand (`foldInspect`), so nothing regresses. Row-form bakes and non-measure colour channels keep
fetching every measure (no split). Correctness of folding over a plane subset (and of the incremental merge)
is pinned by [slab.test.ts](../../web/src/lib/slab.test.ts).
