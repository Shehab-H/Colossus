# Exact high-cardinality numeric filtering

## Objective

Find and prove a production architecture for exact, interactive numeric-range filtering when a map can
contain hundreds of millions to more than one billion facts, each rendered mark can own many facts, and a
filter must recompute the mark's measures over only the facts satisfying an `AND` conjunction of numeric
ranges.

The current performance target is:

- warm server identification and reduction p99 below 50 ms for one interaction;
- ordinary warm click-to-visible-result below 100 ms where response size and client bandwidth permit it;
- exact visibility, inspection, picking, and configured aggregate semantics;
- two CPU cores and no more than 5 GB resident server memory per serving process;
- immutable, source-independent serving artifacts: runtime correctness must not depend on ClickHouse or any
  other source database containing the complete dataset;
- a credible path for 50–60 maps and bursts of approximately 100 unrelated filters per map.

The immediate objective is research proof, not production implementation.

## Non-goals

- Do not approximate numeric boundaries, quantize user ranges into baked buckets, sample facts, or silently
  change visual correctness to satisfy the latency target.
- Do not rebuild the spatial LOD tree after every filter. Geometry, tile identity, mark ordering, and the
  parent/child cover remain immutable.
- Do not assume identical-query cache hits. The expected workload has high-cardinality ranges and little
  overlap between users.
- Do not solve the problem by shipping global fact packs to every browser. The GPU kernel is fast once bytes
  are resident; cold network, decode, and client memory make that general architecture unacceptable.
- Do not treat an in-memory warm benchmark as proof of disk-backed or concurrent production behavior.
- Do not require all data to exist in ClickHouse. The serving artifact must work regardless of source adapter.

## Status

**Active R&D. Last verified: 2026-08-23.**

The leading candidate is now a cost-planned Lance + DataFusion hybrid:

1. spatial/LOD pruning selects the currently drawn immutable tiles;
2. a mark-level multidimensional min/max header eliminates obvious `ANY` failures and answers `ALL` exactly;
3. tiny marks scan contiguous facts directly;
4. category selection routes to physically separate, timestamp-ordered Lance shards before any scalar index;
5. selective predicates use an ordered scalar index, while broad timestamp ranges use category-sharded
   canonical aggregate partials through a narrow DataFusion provider;
6. surviving facts are translated to the selected LOD tiles' `mki` values and reduced from additive partials;
7. the browser receives per-tile visibility bitmaps and only the measure/style values it actually needs.

This architecture is not approved. The real-map Lance + DataFusion experiment rejected a monolithic
independent-index fold, proved the value of physical category/time locality, and established that the proven
aggregate-tree idea still needs one narrow custom DataFusion `TableProvider` for billion-fact broad ranges.

## Work-item index

| Item | Status | Purpose |
|---|---|---|
| [01 — Contract and success criteria](01-contract-and-success-criteria.md) | Active | Freeze exact fact/mark/LOD semantics and what “sub-100 ms” measures. |
| [02 — Established evidence and decisions](02-established-evidence-and-decisions.md) | Active | Preserve measured results, corrections, rejected approaches, and remaining uncertainty. |
| [03 — Leading hybrid architecture](03-leading-hybrid-architecture.md) | Candidate | Define the cost-planned combination of headers, direct scans, ordered indexes, bitmaps, and aggregate BVHs. |
| [04 — LOD integration contract](04-lod-integration-contract.md) | Candidate | Translate fact-level matches into exact per-tile `mki` results without rebuilding the LOD tree. |
| [05 — Production-shaped Lance + DataFusion proof](05-production-shaped-lance-datafusion-proof.md) | Candidate | Layouts A–C measured; global scalar indexes rejected, category/time locality retained, aggregate provider required next. |
| [06 — Disk, memory, and concurrency proof](06-disk-memory-and-concurrency-proof.md) | Candidate | Measure memory-mapped/cold behavior and distinct-query bursts under two cores / 5 GB. |
| [08 — Exact timestamp range fold](08-exact-timestamp-range-fold.md) | Candidate | Use the existing remote-fold seam with a disk-backed exact timestamp aggregate tree; combined context and LOD parity remain to be proved. |

## Evidence already established

- The current reference dataset has 24,257,354 source facts and 2,008,081 distinct geometry marks.
- The current pinned Europe leaf viewport was re-read as 7,250,351 facts and 583,511 rendered marks; every
  rendered mark had at most 32 facts. An older archived remote-fold report calls the same viewport a
  2.4-million-fact fold. That discrepancy must be reconciled before the next benchmark claims production
  parity.
- A clean, mark-contiguous exact `ANY` prototype on the current viewport achieved 23.16 ms p99 for the hardest
  measured four-dimensional query on two pinned logical processors. `ALL` was 5.29 ms p99 from mark headers.
- With 100 million active synthetic facts and 5,000 uniformly distributed heavy marks, the adaptive BVH
  stayed at or below 25.50 ms p99 across the measured query families; the flat path rose to 180.85 ms p99.
- Every adaptive-index visibility bitmap matched the exact flat reference.
- The 100-million-fact prototype's 1.78 GB figure was the entire warm serving representation, not per-user
  memory and not pure index metadata. Approximately 1.53 GB was four raw predicate columns; headers, roots,
  offsets, and BVH nodes were approximately 255 MB. Observed process working set was approximately 2.03 GB.
- A 100-query burst against the real viewport failed the concurrency target: response p95 was 1.64 seconds
  and throughput 60.6 queries/second on two pinned logical processors. Single-query latency is solved more
  convincingly than shared throughput.
- WebGPU scanned 100 million resident atoms in approximately 15–43 ms, but the complete client-side design
  was rejected because unaligned cold queries required hundreds of megabytes of network transfer and up to
  gigabytes of client working memory.
- The existing remote DuckDB fold took approximately four seconds because it reconstructs tile/grid keys,
  mark identities, mark ordering, and grain cells for every request. It is not a fair measurement of a
  purpose-built serving table that persists those identities.
- A single global set of geometry marks cannot filter every LOD. The same fact can map to a merged cell at an
  internal zoom and a real geometry at a leaf. Results must be aligned per `(tile_key, mki)`.
- A one-billion-fact timestamp-only aggregate tree was built in 66.79 seconds using 82.18 MiB peak working
  set. Its raw boundary data plus dense tree occupy 36.627 GiB.
- The worst generated exact second-resolution range read 14 aggregate nodes and 5,479,389 raw boundary facts,
  not the billion-fact table. Cold direct-I/O wall time was 534.94 ms, peak query working set was 77.41 MiB,
  and the query read approximately 510.9 MiB.
- Returning one final Float32 value for each of two million marks produced a 7.63 MiB body. Localhost
  backend-to-paint p50/p95 was 541.2/592.7 ms; a 100 Mbit/s plus 20 ms network model raised p50 to 1.210 s.
- With six queries active, 64 unrelated cold users produced p50 7.493 s and p95 13.104 s while peak working
  set stayed at 723.94 MiB. Disk queueing, rather than the 5 GB RAM limit, dominated concurrency.
- The complete real 24,257,354-fact map converted to a 299.35 MiB Lance dataset in 2.104 seconds with
  207.44 MiB peak working set. Global category/timestamp indexes added 231.34 MiB.
- Independent global Lance indexes were slower than scanning: the one-cell nearly full-range fold took
  669.03 ms warm, read 162.95 MiB, created 783,537 physical ranges, and contributed to a 1.81 GiB process
  peak. The unindexed monolith took 385.90 ms while scanning all facts.
- Physical category routing plus timestamp ordering changed the result: the same exact one-cell fold took
  64.30 ms warm and read 11.72 MiB; a 50% range took 34.21 ms and four contiguous ranges. DuckDB and
  Lance/DataFusion row counts and aggregate checksums matched exactly for both cases.
- With six active queries, 64 unrelated broad requests against the sorted shard achieved 29.73 queries/s,
  p50 1.191 s, p95 2.049 s, and 308.59 MiB peak working set. RAM fits; queued CPU service does not meet the
  interactive tail target.
- A real DataFusion aggregate response containing 2,008,081 Float32 values reached browser next-paint in
  179.4/217.0 ms localhost p50/p95 and 831.6/845.8 ms at 100 Mbit/s plus 20 ms. The 8,032,324-byte body,
  not browser decode, dominated the network-shaped run.

Detailed figures and provenance are in [item 02](02-established-evidence-and-decisions.md) and the raw
[evidence folder](evidence/).

## Current conclusions

1. Exact numeric values do not belong in the categorical/temporal Cartesian slab axes. Their cardinality
   grows with facts and makes the theoretical cell product meaningless.
2. The existing categorical/temporal slab remains valuable. Numeric filtering should be a fact predicate
   feeding the same additive measure contract, not a replacement for the entire companion model.
3. `AND` across four ranges makes one index per dimension useful, but intersection must occur at the fact
   level. Intersecting mark IDs is incorrect when different facts of the same mark satisfy different ranges.
4. Lance remains a good immutable storage layer, but global bitmap/B-tree intersection is not the serving
   layout. Low-cardinality fact-side categories must route to physical shards before the timestamp or numeric
   index is touched; within a time-ranged shard, physical ordering prevents random gathers.
5. Lance indexes identify rows; they do not provide the category-sharded canonical timestamp aggregate tree.
   DataFusion supplies embedded Arrow planning and execution, while a narrow custom `TableProvider` is now
   the evidence-backed next step for broad ranges that would otherwise aggregate fact-volume results.
6. Full column scans are bounded in allocated memory but hungry for disk bandwidth, memory bandwidth, and
   CPU on every unrelated query. They are a valid baseline and small-viewport fallback, not the universal
   billion-fact plan.
7. The LOD tree stays immutable. A filter produces per-tile visibility/value results; the GPU discards dead
   `mki` entries. Filtering must cover both desired tiles and parent/child stand-ins during refinement.
8. Network output creates an independent lower bound. A visibility bitmap for 583,511 marks is about 73 KB,
   while one Float32 value per mark is about 2.33 MB. The latter cannot cross a 100 Mbit/s link within 100 ms.
9. A warm 34.21-ms exact fold is measured for one 384,777-result category shard, but the broad two-worker
   fold is 79.51 ms and full browser p95 is 217 ms even on localhost. The complete sub-100-ms contract is not
   met.
10. The existing remote-fold client contract is the correct integration seam for timestamp ranges: send the
    complete per-fact context and requested tiles, receive final per-`mki` measure columns, keep geometry
    unchanged, and let the GPU recolor and discard marks.
11. A timestamp aggregate tree can answer exact arbitrary-second ranges over one billion facts without a full
    table scan and within the 5 GB RAM budget. Its measured cold single-user latency is about 0.5 seconds, not
    sub-100 ms, and concurrent cold queries are constrained by disk bandwidth.
12. The timestamp-only benchmark is not yet a proof of Colossus semantics. It must preserve existing
    categorical/grain context and generate results for each LOD's `(tile_key, mki)` identities.
13. Inspect is already lazy and context-aware: the current path folds configured inspect measures for the
    clicked tile. Returning only the clicked mark is a possible optimization, not a prerequisite for
    correctness.

## Next decision and action

[Item 05](05-production-shaped-lance-datafusion-proof.md) measured layouts A through C. Layout B's global
indexes failed; layout C's physical category/time locality passed its traversal gate but remained
fact-volume-bound. The next action is layout D: expose the proven timestamp aggregate tree through a minimal
DataFusion `TableProvider`, with Lance supplying category routing, exact boundary facts, and immutable
versioning.

The decisive remaining proof is exact complete-context parity for real persisted `(tile_key, mki)` identities
at leaf and merged internal LODs. It must show that one operator/quarter selection opens only that category's
aggregate roots and boundary data, then compare the result with the current fold for timestamp plus numeric
and categorical conjunctions.

## Acceptance gates for the saga

- Exact fact membership and per-tile `mki` parity for `ANY`, `ALL`, and empty-result cases.
- Exact configured measure semantics, with an explicit deterministic floating-point contract.
- Warm filter + LOD mapping + reduction p99 at or below 50 ms on two CPU cores.
- Ordinary warm end-to-end p95 at or below 100 ms under a declared payload and client-bandwidth contract.
- Cold and prefetched disk behavior measured separately; no warm-cache result presented as cold.
- Resident process memory below 5 GB after exercising every numeric index and active-map partition.
- Database/index size and temporary build space measured at 24.26M and 100M facts, with a defensible
  one-billion-fact projection.
- One, ten, and one hundred unrelated filters measured. If two cores cannot meet the latency target,
  shared traversal or horizontal capacity must have an explicit sizing model.
- Per-tile response format integrates with current LOD cover transitions, GPU discard, and picking.
- All temporary generated data removed after benchmarks; durable evidence retained in this saga.

## Handoff

When resuming, read this README, then items [01](01-contract-and-success-criteria.md),
[04](04-lod-integration-contract.md), [05](05-production-shaped-lance-datafusion-proof.md), and
[08](08-exact-timestamp-range-fold.md). The numeric and timestamp branches share the same exact remote-fold
and LOD result contract. Resume at the category-sharded aggregate-provider proof; do not repeat the rejected
global-index layout.
