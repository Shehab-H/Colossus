# R5 build evidence — cell-run slicing + zstd/dictionary codec (+ per-tile layout)

[REQUIREMENTS.md](REQUIREMENTS.md) Requirement 5's **second half** — the second-level slice directory and the
client slice fetch — plus two format changes it rests on: a **per-leaf-tile** dense/sparse layout choice
(SLAB-FORMAT §3, moved from per-view) and a **zstd + trained-dictionary** codec for the companion pack. R5's
plane splitting and fetch-optimal cell order are already BUILT ([R1-BUILD.md](R1-BUILD.md)); this is the
cell-run layer on top. Fold semantics, the measure grammar, and `tests/fixtures/measure-cases.json` are frozen
— this changes the *physical* companion, never what a fold computes.

Follows the [R1-BUILD.md](R1-BUILD.md) "measured reality check" pattern: measure the real global bakes with a
re-encoder before touching a format, build to the measured target, then re-measure the same way.

## Baseline (measured before any code change)

**Environment.** commit `5ef00a8` (branch point of `claude/r5-slice-zstd`), .NET SDK `10.0.301`, Node
`v22.16.0`, Intel Core (Alder Lake), 12 logical cores, Windows 11. ClickHouse `24.8` in Docker. Real slab
bakes already on disk: mobile-coverage `v20260717T100621Z`, mobile-dominance `v20260717T100349Z` — both
`slab/sparse`, 380 tiles / 255 leaves / 2,008,081 marks / maxZoom 5, axes **operator** (categorical, card 4)
× **quarter** (ordered/cumulative, card 8) → **32 cells**, global occupancy **37.7 %** (< 0.5 → sparse per
the current per-view gate).

**The re-encoder.** [`tools/Colossus.CompanionBench`](../../tools/Colossus.CompanionBench/Program.cs) — a
managed C# tool (dictionary *training* needs a managed zstd lib; Node's zlib has the zstd codec but not the
ZDICT trainer, so this is C# not TS, unlike `bench-companion.ts`). It reads `latest.json → manifest.json`,
ranges each block out of `facts.pack` exactly as the worker does, reconstructs the **dense** cumulative
cell-major planes from the sparse CSR (mirroring `SlabCompanionWriter.WriteDense`), and prices three things
without changing any format. It touches no bake code.

**Commands (exact).**

```
docker compose -f docker/docker-compose.yml up -d          # ClickHouse, if re-baking
# (real bakes already on disk; to reproduce them: for v in mobile-coverage mobile-dominance; do \
#   dotnet run -c Release --project src/Colossus.Bake -- $v; done)
dotnet run -c Release --project tools/Colossus.CompanionBench -- mobile-coverage mobile-dominance
```

### (a) Per-leaf-tile occupancy — how many tiles cross a per-tile dense gate

The dense/sparse gate is `facts / (marks × cells)`. Today it is measured **per view** (both reference views
= 37.7 % → sparse). Measured **per leaf tile**, occupancy is skewed and a minority of tiles clear 0.5 — those
are the ones Work Item A flips to dense and the cell-run path then slices. (Both views share geometry, so the
distribution is identical; they differ only in measures/partials.)

| Per-tile occupancy over 255 leaves | min | p25 | p50 | p75 | p90 | p99 | max |
|---|--:|--:|--:|--:|--:|--:|--:|
| coverage & dominance | 3.1 % | 24.4 % | 32.3 % | 38.8 % | 45.3 % | 66.4 % | 74.7 % |

- **Tiles ≥ 0.5 gate: 13 / 255 (5.1 %)**, holding **5.5 %** of all facts. On this sparse reference data the
  per-tile gate is a *minority* path — most tiles stay sparse and opt out of slicing (SLAB-FORMAT §5). The
  design scenario (100M facts, skewed dense) is where dense dominates; the reference bakes exercise the
  **mechanism** and the worst dense tile.
- The worst tile *by bytes* (`5/17/19`, 124,020 marks) sits at **42.4 %** — below the gate, so it stays
  sparse and is **not** sliced. The worst tile that actually goes dense is `5/25/14` (19,059 marks, 69.4 %);
  that is the honest worst-case for the sliced path and the interaction numbers below use it.

### (b) At-rest bytes — whole-plane blocks vs simulated cell-run blocks, under three codecs

Over the **13 dense-gated tiles**, re-encoded dense, pricing each partial plane whole vs split into per-cell-row
blocks (the R5 slice unit, SLAB-FORMAT §4b), each independently compressed. gzip = `CompressionLevel.Optimal`
(today's pack codec); zstd = level 19; zstd+dict = level 19 with one dictionary trained per view over sampled
cell-row blocks (ZstdSharp `DictBuilder`/ZDICT).

**mobile-coverage** (dict 25,638 B):

| block granularity | gzip | zstd-19 | zstd-19 + dict |
|---|--:|--:|--:|
| whole-plane | 31.58 MB | 22.81 MB | 23.20 MB |
| cell-run (per cell row) | 32.37 MB | 31.69 MB | 31.74 MB |

**mobile-dominance** (dict 114,688 B):

| block granularity | gzip | zstd-19 | zstd-19 + dict |
|---|--:|--:|--:|
| whole-plane | 9.50 MB | 7.03 MB | 7.05 MB |
| cell-run (per cell row) | 9.77 MB | 9.47 MB | 9.24 MB |

- **zstd-19 beats gzip on whole planes by ~28 %** (coverage 31.58 → 22.81 MB) — the at-rest win of Work Item C's
  codec, before any slicing.
- **Slicing costs bytes at rest: cell-run vs whole-plane (zstd+dict) = 1.37× (coverage) / 1.31× (dominance).**
  Small independent blocks lose cross-cell redundancy; that is the price of sliceability, paid **only on the
  dense-gated minority**. The dictionary is what keeps the inflation near 1 — on dominance, whose blocks are
  smaller, the dict recovers cell-run from 9.47 (plain zstd) to 9.24 MB, i.e. below plain zstd; on coverage the
  trained dict is tiny (25 KB) and barely moves it. **The dictionary's value scales with how small the sliced
  blocks are** — modest here, larger in the design scenario's many-small-tile regime.

### (c) Simulated interaction fetch — cell-run slice vs today's whole-plane split

Worst dense-gated tile `5/25/14` (19,059 marks, occ 69.4 %). "Plane-split today" = the whole color-measure
planes' current gzip blocks (what the live client fetches per R1). "Cell-run fetch" = only the cell rows the
compiled context needs (zstd+dict): **2 rows per cumulative plane per selected categorical position** (1 row
when the range starts at bin 0), coalesced. Color measure: coverage `avg_download` (`wavg` → 2 cumulative
planes `swp__download_mbps__tests` + `sum__tests`); dominance `dominant_operator` (`argmax` → 1 plane
`sum__tests`).

**mobile-coverage** — plane-split today = 1.88 MB:

| interaction context | cell-run fetch | vs plane-split |
|---|--:|--:|
| single operator + date window (2 bins) | 0.188 MB | **10.0×** |
| single operator + full range (cumulative from start) | 0.095 MB | 19.8× |
| single operator + single quarter | 0.188 MB | 10.0× |
| date-range window, all operators (no categorical narrowing) | 0.732 MB | 2.6× |

**mobile-dominance** — plane-split today = 0.333 MB:

| interaction context | cell-run fetch | vs plane-split |
|---|--:|--:|
| single operator + date window (2 bins) | 0.048 MB | **7.0×** |
| single operator + full range (cumulative from start) | 0.024 MB | 13.6× |
| single operator + single quarter | 0.048 MB | 7.0× |
| date-range window, all operators (no categorical narrowing) | 0.178 MB | 1.9× |

**The R5 acceptance target — a single-select + date-range interaction ≥5× on top of plane splitting, on dense
tiles — is met on both reference views: 10.0× (coverage) / 7.0× (dominance).** The ratio comes from reading 2
of 8 ordered bins in 1 of 4 categorical runs (4 cell rows of 64) instead of the whole plane; compression on
tiny blocks recovers less, so the raw 16× lands at 7–10×. A range with no categorical filter reads all 4 runs
(2.6× / 1.9×) — expected, and still a win. This is the number the build must reproduce on the re-baked views.

> **Reproduce against a fresh bake:** the same `dotnet run … tools/Colossus.CompanionBench -- <views>` command
> reads whatever `facts.pack` is on disk. After Work Items A–C land and the views are re-baked, re-run it for
> the After tables (below), and re-run on any real dense-heavy source to see the design-scenario regime.

## After (built) — measured on the same machine + re-encoder as the Baseline

**Re-baked.** mobile-coverage `v20260717T212038Z`, mobile-dominance `v20260717T212912Z`, with Work Items A–C
live (per-leaf-tile gate, cell-run slice directory, zstd+dict codec). Fidelity witness **Σcnt == source rows
PASS** on both (2,008,081 leaf marks → 24,257,354 source rows), `overBudget=0`. The two **row-form** views
(geonames, ookla-fixed) re-baked **byte-identical** (facts.pack Δ = 0, total dir Δ = 0) — the format changes
touch only the group regime, as required. `--after` reads the re-baked pack's directory (no block decode) for
the tables below.

### (a) Per-leaf-tile occupancy — confirmed on the re-bake

Same gate (0.5), same **13/255 dense leaves** the baseline predicted. But R5 packs the internal levels too, and
an interior tile aggregates its descendants' marks into the same 32 cells, so its occupancy is higher: **32 of
the 125 internal tiles also cross the gate → 45/380 tiles dense** overall. This is the fact the leaf-only
baseline could not see, and it drives the at-rest result below.

### (b) At-rest bytes — the real re-baked pack (all tiles, actual codec)

Measured pack + total-dir sizes, old bake vs re-bake:

| view | regime | old pack (gzip) | new pack (zstd+dict) | Δ pack | total dir Δ |
|---|---|--:|--:|--:|--:|
| mobile-coverage | slab | 727 MB | 833 MB | **+106 MB (+14.6 %)** | 1589 → 1695 MB |
| mobile-dominance | slab | 222 MB | 266 MB | **+45 MB (+20 %)** | 783 → 828 MB |
| geonames | row | — | — | 0 (no companion) | 705 → 705 MB |
| ookla-fixed | row | — | — | 0 (no companion) | 1306 → 1306 MB |

**The pack grew at rest.** Decomposing mobile-coverage by the layout each tile took:

| tile bucket | old gzip | new zstd+dict | ratio |
|---|--:|--:|--:|
| 335 sparse tiles | 491 MB | 478 MB | 0.97× |
| 45 dense tiles | 236 MB | 354 MB | **1.50×** |

zstd shrinks the sparse majority by only **2.6 %** — already-compact sparse CSR Arrow has little left to give — while
the 45 dense tiles grow 1.50×. And the dense growth is concentrated in the internal tiles: of the +119 MB, the
**13 dense leaves add 30 MB** (the intended sliced path, baseline §a), the **32 dense parents add 324 MB**.
Dense stores a cumulative plane *per mark*, so a high-mark-count parent inflates far more than its occupancy
ratio implies. This is the honest cost on **sparse** reference data (37.7 % global occupancy): at rest the
initiative is a net *loss* here. The at-rest win it was designed for — zstd's ~28 % whole-plane gain (baseline
§b) — only materialises on genuinely dense data, where dense is the majority and slicing pays for itself; on
this data the payoff is the interaction fetch, not disk.

### (c) Interaction fetch — measured on the actual `sliceEntries` (worst dense tile `5/25/14`)

Same tile and contexts as the baseline §c, now priced from the **re-baked** cell-row blocks (zstd+dict), whole
dense color planes as the reference:

**mobile-coverage** (dict 114,688 B) — plane-split reference = 2.67 MB:

| interaction context | cell-run fetch | vs plane-split |
|---|--:|--:|
| single operator + date window (2 bins) | 0.20 MB | **13.0×** |
| single operator + full range (cumulative from start) | 0.10 MB | 25.8× |
| single operator + single quarter | 0.20 MB | 13.0× |
| date-range window (2 bins), all operators | 0.80 MB | 3.3× |

**mobile-dominance** (dict 79,994 B) — plane-split reference = 0.62 MB:

| interaction context | cell-run fetch | vs plane-split |
|---|--:|--:|
| single operator + date window (2 bins) | 0.06 MB | **9.6×** |
| single operator + full range (cumulative from start) | 0.03 MB | 18.9× |
| single operator + single quarter | 0.06 MB | 9.6× |
| date-range window (2 bins), all operators | 0.25 MB | 2.5× |

**The R5 acceptance target — single-select + date-range ≥5× on dense tiles — is met and exceeded: 13.0×
(coverage) / 9.6× (dominance).** These beat the baseline's *simulated* 10.0× / 7.0×: the trained dictionary and
the real block boundaries compress the tiny cell-row blocks better than the re-encode estimate did, so more of
the raw 16× (2 of 8 bins × 1 of 4 operator runs) survives to the wire. The no-categorical-filter range still
reads all four runs (3.3× / 2.5×) — a win, just a smaller one.

### Verdict — what improved, by how much, and what didn't

- **Interaction fetch (the target): improved 13.0× / 9.6×** for single-select + date-range on the worst dense
  tile — above the ≥5× bar and above the pre-build estimate.
- **Fold correctness: unchanged** — byte-identical to whole-slab folds (shared fixture, both languages); witness
  Σcnt PASS on the re-bake.
- **Row-form bakes: unchanged** — geonames / ookla-fixed re-baked byte-for-byte; the seam and manifest gates hold.
- **At-rest size on this sparse data: regressed** — pack +14.6 % (coverage) / +20 % (dominance), because the
  per-tile gate also flips 32 high-mark-count internal tiles dense (+324 MB), which zstd's 2.6 % sparse win can't
  offset. Restricting dense to leaf tiles would return at-rest to ≈flat while keeping the drill-in win; left as a
  follow-up (the gate is a one-line change), not taken here.
