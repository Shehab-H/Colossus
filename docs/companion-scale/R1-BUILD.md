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

<!-- The After section + comparison table are appended once the slab build lands and is re-measured
     on this same machine with this same harness. -->
