# 08 — Exact timestamp range fold

**Status:** Candidate — timestamp-only proof established; combined-context and LOD parity remain unproven.

**Last verified:** 2026-08-23.

## Conclusion

Exact second-resolution timestamp filtering does not require changing the map, rebuilding geometry, sending
facts to the browser, or adding timestamp values to the Cartesian slab axes. Colossus already has the right
frontend contract: submit the complete active per-fact context to the remote fold, receive final values in
the existing per-tile `mki` order, replace the measure column for the immutable marks, and let the GPU apply
color, visibility, and per-mark predicates.

The remaining problem is the backend fold. The current DuckDB path reconstructs spatial and mark identities
while scanning facts on every interaction. A disk-backed timestamp aggregate tree proved that one exact
timestamp-only weighted aggregate over one billion facts can instead read bounded precomputed partials plus
the raw facts in at most two boundary days. It fits the 5 GB server-memory budget, but the proof has not yet
shown exact interaction with the existing categorical/grain Cartesian context or alignment to every LOD's
`(tile_key, mki)` identities.

This is therefore a credible backend optimization behind the existing remote-fold seam, not yet a
production-ready replacement for that fold.

## Existing integration surface

The relevant client path already exists:

- [`splitFilters`](../../../web/src/features/map-viewer/engine/channels.ts) separates per-fact fold context
  from per-mark GPU predicates.
- [`useMeasureFold`](../../../web/src/features/map-viewer/hooks/useMeasureFold.ts) selects either the local
  slab fold or the remote fold while preserving the same per-mark result shape.
- [`foldRemote`](../../../web/src/features/map-viewer/engine/remoteFold.ts) sends the full active per-fact
  context and requested viewport tiles, then decodes measure columns ordered by tile and `mki`.
- [`tileDeckData`](../../../web/src/features/map-viewer/engine/deckData.ts) installs those folded values as
  `getScaleValue`; geometry and mark ordering do not change. The GPU maps the values to colors and discards
  `NaN` entries.
- [`MapViewerPage`](../../../web/src/pages/maps/MapViewerPage.tsx) already joins filtering, folding, drawing,
  picking, and inspect around this contract.

The server seam also exists in
[`FoldController`](../../../src/Colossus.Server/Controllers/FoldController.cs) and
[`DuckDbFoldExecutor`](../../../src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs). The backend must
evaluate the complete active per-fact context—not timestamp alone—and return the final measure values for
the requested tiles. The browser does not need Cartesian cells or fact rows when this path is used.

## Runtime ownership

| Concern | Owner | Behavior |
|---|---|---|
| Timestamp range and other per-fact filters | Server fold | Evaluate one exact `AND` context and recompute configured measures. |
| Categorical/grain Cartesian semantics | Server fold | Preserve the current semantics while applying the timestamp restriction. |
| Geometry, tile cover, and mark order | Existing immutable map artifacts | No rebuild or transfer on interaction. |
| Final measure value per visible mark | Server response | One typed value per requested `(tile_key, mki, measure)`; `NaN` means no surviving facts. |
| Color scale and visibility | Browser GPU | Recolor existing marks and discard hidden marks from the replacement values. |
| Per-mark filters | Browser GPU | Continue to run locally; they are not folded into fact aggregation. |
| Inspect measures | Existing lazy inspect request | Fold only the clicked tile under the same complete context, then read the picked `mki`. |

For one Float32 measure and two million marks, the uncompressed response floor is 8,000,000 bytes
(7.63 MiB). Compression may reduce transfer size depending on value distribution, but it must not be counted
on for correctness or for worst-case network sizing.

## Inspect behavior

Inspect does not require all inspect measures in every map response. The existing click path calls
`foldInspect` for the clicked tile, using the same full per-fact context and the configured inspect measures.
Static and per-mark columns remain lazy-loaded from the render tile.

The current remote inspect shape returns per-mark measure arrays for the whole clicked tile and then selects
the clicked `mki` in the browser. That is correct, but a root or otherwise large tile can do more work than
necessary. A future mark-addressable request could return only the picked row. That optimization would need
the timestamp index to address boundary facts by mark, or a short-lived reuse of the map query result.

## Measured timestamp-only proof

The benchmark implementation is in
[`TimestampTreeBench.cs`](../../../tools/Colossus.NumericFilterBench/TimestampTreeBench.cs),
[`TimestampTreeE2eServer.cs`](../../../tools/Colossus.NumericFilterBench/TimestampTreeE2eServer.cs), and
[`bench-timestamp-tree-e2e.mjs`](../../../web/scripts/bench-timestamp-tree-e2e.mjs). It changed benchmark code
only; no production path was modified.

The retained artifact contains one billion synthetic facts, two million marks, 365 days, exact timestamps to
the second, raw boundary records grouped by day, and a dense per-mark segment tree of weighted-aggregate
partials. The aggregate stores a double numerator and 64-bit denominator. An independent 10-million-fact
smoke corpus matched a literal raw scan checksum.

| Phase or resource | Measured result |
|---|---:|
| Artifact build | 66.79 s |
| Peak builder working set | 82.18 MiB |
| Raw boundary records | 14.901 GiB |
| Dense aggregate tree | 21.726 GiB |
| Total artifact | 36.627 GiB |
| Cold direct-I/O worst absolute range | 534.94 ms |
| Cold query CPU | 234.38 ms, about 0.44 of one logical core over wall time |
| Cold query peak working set | 77.41 MiB |
| Cold query physical/logical read | 510.91/510.85 MiB |
| Cold direct-I/O repeat | 445.03 ms |
| Forced disjoint-range delta | 613.15 ms first, 571.53 ms repeat |
| Eight users, maximum six active | p50 1.354 s, p95/max 1.927 s, peak 344.04 MiB |
| Sixty-four users, maximum six active | p50 7.493 s, p95 13.104 s, peak 723.94 MiB |

The absolute worst-range generator selected the interval requiring the maximum 14 canonical tree nodes plus
two almost-full boundary days containing 5,479,389 raw facts. It did not scan the billion-fact table. Under
concurrency, RAM remained comfortably below 5 GB; the NVMe queue and roughly 0.5 GiB read per cold query
became the limiting resources rather than CPU or allocation.

The server-to-browser experiment returned only final Float32 values for two million marks:

| Path | p50 | p95 | Notes |
|---|---:|---:|---|
| Localhost fetch through next paint | 541.2 ms | 592.7 ms | Backend p50 494.4 ms; 7.63 MiB body. |
| 100 Mbit/s plus 20 ms network model | 1.210 s | 1.242 s | Body transfer p50 691.9 ms. |

Decode was below 1 ms and buffer upload plus paint was a few milliseconds. Chromium used SwiftShader, so
this validates response, decode, and replacement-buffer mechanics rather than production physical-GPU draw
performance. After eight browser queries, the persistent benchmark server used 232.35 MiB working set,
240.24 MiB peak working set, and 2.406 seconds of process CPU.

## What the proof establishes

- Exact arbitrary timestamp boundaries can avoid both a full table scan and a high-cardinality Cartesian
  timestamp axis.
- Server work is bounded by `O(log days)` complete aggregate nodes, at most two raw boundary partitions,
  and the number of output marks.
- The result naturally fits the current remote-fold response: final per-mark measure columns rather than
  facts or Cartesian cells.
- Server RAM and CPU fit the stated 5 GB constraint for the tested timestamp-only query. Cold disk I/O and
  response transfer, not memory, set the single-query latency floor.
- Unrelated concurrent cold queries remain expensive. Six simultaneous disk readers do not create sixfold
  throughput; they create an NVMe queue and multi-second tail latency.

## What remains unproven

1. The dense tree is keyed by one global mark set. Colossus needs results aligned separately to every active
   LOD's `(tile_key, mki)` order.
2. The timestamp-only weighted aggregate does not preserve the current categorical/grain Cartesian cells.
   A production index must either store additive partials by the required low-cardinality grain cell or
   combine timestamp pruning with a second exact structure without returning to a fact scan.
3. Only one weighted-average partial contract was measured. Exact `sum`, `count`, `average`, `min`, `max`,
   share, and argmax-style measures need explicit merge rules and size measurements.
4. Live append, late data, index compaction, artifact versioning, and concurrent version swaps were not
   benchmarked.
5. The tested response contained one measure. Multiple active/inspect measures multiply output and merge
   work unless they are requested lazily.
6. The current scatterplot/point rendering call does not pass the folded override that the polygon path
   passes. That is a small integration gap, not an indexing limitation.
7. The current inspect request returns a whole tile. Mark-only inspect is an optimization opportunity, not
   a correctness requirement.

## Decision boundary

Do not remove exact timestamp filters based on the current evidence. The backend aggregate-tree direction is
viable enough to continue, and the frontend map-update and inspect contracts already exist. Do not claim the
one-billion-fact benchmark as production proof until the index reproduces the real map's complete per-fact
context and per-LOD results exactly.

The next decisive evidence is a production-shaped slice keyed by real `(tile_key, mki, grain_cell)` identities.
It must compare a timestamp-tree fold with the existing exact fold for timestamp plus operator, quarter, and
other active per-fact filters; cover leaf and merged LOD tiles; include a clicked-mark inspect; and report cold,
warm, network, memory, CPU, and concurrent latency separately.
