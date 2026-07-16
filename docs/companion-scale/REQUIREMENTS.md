# Companion Scale — Requirements

**Status: REQUIREMENTS (approved direction, not yet planned or built).** This file says *what* must
be true and *why*; it is not an implementation plan. Measure/fold semantics remain normative in
[VIEW_CONFIG.md](../VIEW_CONFIG.md) §1/§4 and are **not changed** by anything here.
[RULES.md](../RULES.md) is the authority; nothing here overrides R1–R7.

## The problem

The group regime writes a fact companion (`z/x/y.facts.arrow`) beside every render tile: one row
per `(mki, grain values…)` with repeated key columns (`mki` i32, dict indices i32, DateDay i32) and
f32/i32 partials, uncompressed Arrow IPC, one file per tile. At the leaf level every source fact
appears verbatim, so companion bytes scale linearly with fact count at ~28–36 B per fact:

- **Reference today:** 7.6M facts / 627K marks → manageable.
- **Design scenario:** 100M facts / 800K marks (~125 facts per mark) → **~3 GB** of leaf
  companions, and a dense leaf tile (tens of thousands of marks × ~100 grain cells) is a
  **tens-of-MB fetch for one tile**. That single-tile number is the one that hurts; no client ever
  downloads the total.

Internal-level companions are grid-collapsed and bounded per tile — the leaf level dominates.
Half of every row is key material that a better layout makes implicit.

## What must not change

1. Fold semantics, the measure grammar, and `tests/fixtures/measure-cases.json` — byte-for-byte.
2. The client seam `fold(measures, context) → columns` and the `mki` join to rendered marks.
3. `(version, tileKey)` as the only data identity (RULES R4 note; gpu-residency invariant).
4. Lossless only: partials remain exact. No quantization, sketches, or sampling in this initiative.
5. Data-agnostic (RULES-level rule for this repo): no channel names, dataset shapes, or
   role-specific special cases in any format logic. Layout decisions are *measured from the staged
   data at bake*, never authored or hardcoded.

## The axis model (normative for this initiative)

Every companion grain channel is one **axis** of a per-tile cell space. Axes have exactly two
kinds, distinguished by filter algebra, never by data type:

- **Categorical axis** — dict channel, filtered by equality (later multi-select). Domain = the
  existing canonical dictionary order. A context selects a subset of positions.
- **Ordered axis** — channel filtered by range. Temporal channels are an *instance* of this kind,
  not a special case; future binned-numeric `range` channels land here with no format change.
  Domain = an ordered bin list recorded in the manifest. A context selects a contiguous span.

The spec must answer these degenerate cases without special-casing (spec test: describe a view
with **no temporal channel, one 5,000-cardinality dim, and 2% occupancy** without an if-statement
naming channels):

| Case | Required behavior |
|---|---|
| No ordered axis | No cumulative planes; equality-only folds. |
| Only an ordered axis | Single-axis cell space; range folds. |
| No perFact axes at all | Baked default-context values are already the answer — emit **no** companion. |
| High-cardinality axis / sparse cross product | Sparse layout selected by measurement (Req 1). |

## Requirement 1 — Slab companion format

Replace the row-form companion with a **slab**: per tile, one plane per partial over
`cells × marks`, where cells enumerate the axis cross product in canonical order and marks are
addressed by `mki`. Key columns disappear into array indexing.

- **Two physical layouts, one logical model.** *Dense:* contiguous planes, cell-major. *Sparse:*
  per-mark CSR (offsets + cell ids + partials); the current row format may serve as the v0 sparse
  fallback. The layout is chosen **per view at bake** from measured occupancy
  (`facts / (marks × cells)`) and cell count; the manifest records the choice and the axis
  metadata (kind, domain reference, bin list, cumulative flags); the client branches on the
  manifest, never on data shape.
- **Cumulative ordered-axis planes.** Along an ordered axis, planes for subtractable partials
  (`sum`, `cnt`, `swp`) are prefix sums — a range fold is two slice subtractions. `min`/`max`
  planes are never cumulative and keep the scan path. With multiple ordered axes, one is chosen
  cumulative (bake decision, recorded); the rest scan.
- **Narrow types where lossless:** `mki` is tile-local (u16 when marks-per-tile permits, u32
  fallback); cell ids sized to the cell count; partial planes stay f32/i32 exactly as today.
- **Cross-language authority.** The slab layout gets a written spec and a shared fixture pinned by
  both C# and TS tests — the fourth cross-language authority, alongside tiling, schema, and
  measures.
- **Worker + zero-copy discipline unchanged:** fetch/decode on the tile worker pool, typed arrays
  end to end, transfer without copy; fold-result and companion caching keys unchanged.

**Acceptance:** fold results identical to the row-form fold on the shared fixtures; verifier's
companion witness becomes `Σ cnt plane == source rows`; on the design scenario, dense leaf bytes
shrink ≥2.5× and the fold is vectorizable (no per-row key decoding); the dense plane layout is
directly uploadable as GPU textures/buffers for the deferred GPU fold
([gpu-residency PHASE-5](../gpu-residency/PHASE-5-deferred-frontier.md) §5.3–5.4) without
reshaping.

**Measured reality check (2026-07-13, re-encoder over the real global bakes; every unit
round-trip verified):** at the reference views' measured 32–42% occupancy, sparse (CSR) beats
dense on disk, and gzipped wire bytes are near-flat vs packed rows (coverage leaves 367.1→355.2
MB, dominance 121.1→109.3 — gzip already eats the sorted key columns). R1's measured value is the
**compute substrate**: raw decode/memory bytes −18% (coverage) / −38% (dominance), O(1) indexed
fold, GPU-uploadable planes — plus what it unlocks in Requirement 5 (plane splitting, measured
50.5/25.3 → 7.8/7.5 MB worst interaction fetch) and compressed internal companions (713.8→385.4 /
345.4→125.5 MB, shipped uncompressed today). Benchmarks must report these axes; the ≥2.5× dense
wire target applies only to the hypothetical high-occupancy design scenario.

## Requirement 2 — Leaf packaging: one slab file + range directory

**Status: BUILT 2026-07-12** (ahead of R1 — it packages whatever the leaf companion format is, today
the row form). Bake: `CompanionPackWriter` (gzip blocks in manifest tile order, `facts.pack`), directory
in `manifest.companionPack`. Client: `fetchArrowBlock` (Range + `DecompressionStream`) on the tile
worker pool; service worker caches blocks per tile. Verifier reads leaf witnesses through the pack.

Leaf companions collectively hold every fact exactly once; stop paying per-file overhead for them.

- One archive per (view, version): leaf slabs concatenated in tile order, each tile's block
  **independently compressed** (an encoding the browser-native `DecompressionStream` accepts — no
  new dependencies), with a directory (`tileKey → offset, length`) in the manifest or a sidecar.
- Client fetches by HTTP `Range` and decompresses in the worker. Per-tile caching is untouched:
  the cache key is still the tile, never the byte range. The service worker caches by tile key.
- Internal-level companions stay per-tile files — they are small and grid-bounded.
- Compression must live *inside* the archive: `Content-Encoding` does not compose with ranged
  requests. On-prem serve (R7): nginx and Kestrel static files serve ranges natively — verify the
  server config; per-file layout remains supported (absence of a directory selects it).
- This subsumes the old fetch-locality **4.3 pack container** sketch (see gpu-residency history);
  render tiles may later adopt the same container, but that is out of scope here.

**Acceptance:** leaf file count collapses to one archive + directory; on-disk leaf bytes shrink
further (compression on sorted planes); a version flip invalidates cleanly; dev server and
production serve both honor ranges.

## Requirement 3 — Materialized 1-D slices (OPTIONAL, measurement-gated)

Pre-fold each single-axis context at bake (one f32 column per mark per measure per slice) so the
most common interaction never fetches a companion. **Gate:** build only with measurements, taken
after Requirements 1–2 land, showing single-filter contexts dominate usage *and* companion
transfer is still the interaction bottleneck. Do not build on speculation.

## Requirement 4 — Remote fold routing (APPROVED 2026-07-16)

**Status: owner signed off 2026-07-16, waiving the entry criteria below** — building ahead of a
proven over-budget view. This is the engine's first runtime compute component; RULES R7's static
tile serve is untouched (the fold endpoint is additive, tiles remain immutable static files).

The planner prices a view's slab; above budget, folds execute remotely (server DuckDB over the
baked facts Parquet) behind the same `fold(measures, context) → columns` seam, shipping folded
columns (~marks × measures × 4 B) instead of facts. **Original entry criteria (waived):** a real
view whose slab exceeds acceptable client budgets after Requirements 1–2, plus owner sign-off on
the first runtime compute component (R7 currently means static serve only). Recorded so
Requirements 1–2 are not designed in ways that foreclose it — nothing about the slab format may
assume folds are client-side.

## Requirement 5 — Context-sliced companion fetch (added 2026-07-16)

Requirement 1 fixes bytes at rest and fold cost; Requirement 4 moves the fold. Neither changes
the interaction scaling law: a filter change still transfers the whole cell space
(`cells × marks × planes × 4 B` per tile) when the active context reads only a sliver of it.
This requirement makes interaction transfer proportional to the **selection**: a cumulative
range fold needs exactly the `hi` and `lo−1` cell rows per selected categorical position, and
those rows are contiguous bytes in a cell-major slab — fetch only them.

- **Plane splitting first (co-build with Requirement 1; measured 2026-07-13).** The pack
  directory carries per-plane byte ranges so a fold fetches only the planes its active measures
  need. Measured on the real global bakes: worst single-tile interaction fetch 50.5 / 25.3 MB →
  **7.8 / 7.5 MB**; coverage two-plane leaf total 222 MB vs the 1081 MB shipped today. Internal-
  level companions gain compression in the same stroke (uncompressed today; gzip alone
  713.8→385.4 / 345.4→125.5 MB).
- **Fetch-optimal cell order (constrains Requirement 1, normative).** The canonical cell
  enumeration orders the ordered axis fastest, categorical axes slower — an equality selection
  is one contiguous run per selected position; a range selection a contiguous sub-run within it.
  The manifest records the order; the choice is derived from the axis model, never authored, and
  contains no channel names (data-agnostic).
- **Second-level pack directory (extends Requirement 2).** Compression blocks at
  `(tileKey, plane, cell run)` granularity with a directory beside the tile directory; same
  Range + `DecompressionStream` machinery, one level finer. Whole-tile blocks remain the
  fallback — absence of the slice directory selects them.
- **Slice fetch on the client.** The tile worker fetches only the runs the compiled context
  needs: two rows per cumulative plane per selected categorical position; the `[lo..hi]` run for
  scan (`min`/`max`) planes. Multi-select (future) fetches one run per selected position;
  adjacent runs must coalesce, parallel small ranges are acceptable.
- **Cache identity unchanged.** `(version, tileKey)` remains the only data identity; slices
  cache *under* the tile entry (sub-keys), never as new identities. Fold-result caching keys
  unchanged.
- **Sparse layout may opt out.** CSR is mark-major and hostile to cell slicing; the bake's
  measured layout decision also decides sliceability, recorded in the manifest. A sparse view
  falls back to whole-block fetch (its blocks are small by construction).
- **Relationship to the sheet.** Likely subsumes Requirement 3 (a materialized 1-D slice is the
  two cumulative rows this fetches on demand) — R3 stays gated and is revisited only if
  measurements after R1+R5 still show a bottleneck. Pushes R4's original entry criteria further
  out; R4 remains the priced fallback for views over budget even with slicing.

**Acceptance:** fold results byte-identical to whole-slab folds on the shared fixtures; plane
splitting reproduces the measured ≥3× worst-tile interaction-fetch shrink on the reference bakes
(50.5/25.3 → ~7.8/7.5 MB); cell-run slicing shrinks a single-select + date-range interaction a
further ≥5× on top of plane splitting, measured; no runtime compute on this path (R7 static serve
intact); dev server and production serve honor the sliced ranges; verifier unchanged.

## Verification (applies to every requirement)

- `dotnet test` · `cd web && npx tsc -b && npm run lint && npm run test` — green.
- `dotnet run --project src/Colossus.Bake -- verify` against a fresh bake of all registered views;
  row-regime views byte-for-byte unchanged.
- Backward compatibility: the client reads row-form companions (current bakes) until every
  group-regime view is re-baked; the manifest gates the format.
- Browser scenarios: the group-regime reference view renders; context filtering recolors
  identically before/after (same folded values); no console errors.

## Cost estimate (from the design discussion, 2026-07-12)

| Requirement | Estimate | Risk |
|---|---|---|
| 1 — Slab format (dense + sparse, axis model) | ~9–12 focused days | moderate (format redesign; semantics pinned by fixtures) |
| 2 — Packaging + ranges + in-archive compression | ~4–5 days | low |
| 3 — 1-D slices | ~4–5 days *if* gated in (likely subsumed by 5) | low |
| 4 — Remote fold (planner pricing + server executor + client route) | ~5–7 days | moderate (first runtime compute; semantics pinned by fixtures) |
| 5 — Context-sliced fetch (cell order + slice directory + client slices) | ~3–5 days | low–moderate (extends R2 machinery; constrains R1's physical order) |
