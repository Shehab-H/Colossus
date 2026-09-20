# 05 — Production-shaped Lance + DataFusion proof

**Status:** Candidate — layouts A through C were measured; direct scalar-index serving is rejected for
billion-fact broad ranges, while category/time clustering is retained for the aggregate-provider experiment.

**Last verified:** 2026-08-23 against current Lance and Apache DataFusion documentation.

## Decision

The experiment used Lance for immutable indexed storage and Apache DataFusion for in-process Arrow query
execution. It established that these components are useful, but that independent scalar indexes do not own
the complete billion-fact fold. The retained direction is category-routed, timestamp-ordered Lance storage,
DataFusion execution, and the smallest specialized aggregate-index provider needed for broad ranges.

This choice does not assume that scalar row indexes solve aggregation. Lance indexes return matching row
identities. A broad timestamp or numeric range may still select hundreds of millions of facts, so the test
must measure the complete fold rather than stop after row identification.

## Why this pair

Lance provides an immutable columnar dataset plus scalar index structures layered over stable row identities:

- bitmap indexes for low-cardinality category/cell codes;
- B-tree indexes for timestamps and high-cardinality scalar ranges;
- filter pushdown and a native DataFusion `TableProvider`;
- Arrow-native batches without translating through a separate database wire format.

DataFusion provides the embedded Rust execution layer:

- Arrow memory representation;
- SQL, expressions, aggregation, streaming, and multithreaded execution;
- filter and projection pushdown;
- custom `TableProvider` and physical-plan extension points if the off-the-shelf Lance scan is insufficient.

Official references:

- [Lance index model](https://lance.org/format/index/)
- [Lance and DataFusion integration](https://lance.org/integrations/datafusion/)
- [Apache DataFusion introduction and extension surface](https://datafusion.apache.org/user-guide/introduction.html)

## Exact question

Can Lance + DataFusion answer the existing remote-fold contract within the resource envelope while making a
fact-side category selection reduce timestamp index traversal, bytes read, and merge work?

The timed operation is:

```text
requested viewport tiles
+ complete per-fact context
    category/cell equality or IN predicates
    exact arbitrary timestamp range
    applicable exact numeric ranges
→ index-pruned facts or aggregate partials
→ exact configured measure reduction
→ requested (tile_key, mki) order
→ final typed per-mark measure columns
```

## Measured result

The isolated benchmark is in
[`Colossus.NumericFilterBench`](../../../tools/Colossus.NumericFilterBench). It uses Lance 10.0.0,
DataFusion 54.1.0, Arrow 58.4.0, a release-optimized Rust `cdylib`, and the existing C# benchmark process.
No production code was changed. The deployed server would ship the native library; Rust and `protoc` are
build dependencies, not runtime dependencies.

The source was the complete 24,257,354-fact reference Parquet file with 2,008,081 real `(x, y)` marks,
four operators, and eight quarter codes. Because it has no second-resolution timestamp, the benchmark
derived a deterministic high-cardinality second value from the real quarter and numeric measures. Real
operators, quarters, mark coordinates, weights, and download values were preserved. This proves the engine
and physical-layout behavior for arbitrary range predicates; it does not claim that the synthetic timestamp
distribution is the production distribution.

Two independent DuckDB brute-force queries reproduced the DataFusion results exactly after both engines
were pinned to the same `Float32 → Float64 → round → Int64` aggregate contract:

| Query | Rows | DuckDB and Lance/DataFusion checksum |
|---|---:|---:|
| one category cell, nearly full quarter | 784,239 | `0x27996165E712ECC4` |
| one category cell, middle 50% | 384,777 | `0xF5FD772C9008AC98` |

This is exact fact-membership and aggregate parity for the measured leaf-style grouping. It is not yet
parity for persisted `(tile_key, mki)` identities, merged internal LODs, multi-value category `IN` requests,
or every configured measure.

### Bake and storage

| Artifact or phase | Wall time | Peak working set | Retained size |
|---|---:|---:|---:|
| Complete unindexed Lance conversion | 2.104 s | 207.44 MiB | 299.35 MiB |
| Global operator bitmap + quarter bitmap + timestamp B-tree | 11.894 s | 219.64 MiB | +231.34 MiB |
| Global dataset after indexes | — | — | 530.68 MiB |
| One unsorted category shard | 279 ms | 132.57 MiB | 12.16 MiB |
| Unsorted shard timestamp B-tree | 104 ms | 64.47 MiB | +5.46 MiB |
| Timestamp sort of that shard | 569 ms | 362.83 MiB | 39.27 MiB temporary Parquet |
| Sorted Lance shard | 143 ms | 97.21 MiB | 15.32 MiB |
| Sorted-shard timestamp B-tree | 76 ms | 64.60 MiB | +5.33 MiB |

The global timestamp B-tree's `page_data.lance` is 217.70 MiB; the two category bitmaps and metadata account
for most of the remaining 13.64 MiB. The measured sorted shard plus timestamp index occupies 20.65 MiB for
784,255 facts. Sorting harms compression of some non-time columns, so a full sharded projection must be
measured rather than multiplying this shard blindly.

### Query behavior on twelve logical workers

The process was pinned to twelve logical processors, representing the requested six-physical-core host.
Times below are repeat/warm values unless marked first or cold-ish.

| Layout and query | Rows scanned | Physical ranges | Process read | Wall | Process peak |
|---|---:|---:|---:|---:|---:|
| Unindexed monolith, all cells/full range | 24,257,354 | 25 | 273.25 MiB | 465.19 ms | 347.33 MiB |
| Unindexed monolith, one cell/full range | 24,257,354 | 25 | 299.10 MiB | 385.90 ms | 347.33 MiB |
| Global indexes, all cells/full range | 24,257,354 | 25 | 162.95 MiB | 1,411.09 ms | 1,041.86 MiB after first load |
| Global indexes, one operator/full range | 6,536,566 | 6,318,857 | 162.95 MiB | 2,589.17 ms | 1,813.75 MiB process peak |
| Global indexes, one cell/full range | 784,239 | 783,537 | 162.95 MiB | 669.03 ms | 1,813.75 MiB process peak |
| Global indexes, one cell/middle 50% | 384,777 | 384,624 | 162.95 MiB | 446.01 ms | 1,813.75 MiB process peak |
| Unsorted physical shard, full range, no index | 784,255 | 1 | 12.15 MiB | 56.91 ms | 131.63 MiB |
| Unsorted shard B-tree, middle 50% | 384,777 | 176,792 | 8.59 MiB | 114.71 ms | 141.83 MiB |
| Sorted shard B-tree, full range | 784,239 | 1 | 11.72 MiB | 64.30 ms | 139.94 MiB |
| Sorted shard B-tree, middle 50% | 384,777 | 4 | 5.85 MiB | 34.21 ms | 139.94 MiB |
| Sorted shard, full range after 21.73 GiB cache pollution | 784,239 | 1 | 17.08 MiB first hit | 119.21 ms | 150.94 MiB |

The global indexes are a clear regression. They identify fewer logical rows, but values are scattered across
all fragments. Lance must load a 217.70 MiB timestamp index and gather hundreds of thousands to millions of
row ranges. Index residency and temporary gather state explain the 1.81 GiB process peak. The index did not
make a selected category restrict physical traversal in the required way.

Category routing changes the result. Opening only one physical category shard reduced a full-range request
from approximately 299 MiB to 12 MiB read before adding any timestamp index. Ordering that shard by timestamp
then made the B-tree output contiguous: the 50% range used four ranges instead of 176,792. This passes the
category-traversal gate for the measured single cell.

On two DataFusion workers, the sorted/indexed shard took 79.51 ms warm for the nearly full range and 53.03 ms
for the middle 50%, with a 133.04 MiB process peak. It therefore misses the saga's 50 ms p99 compute target
for the broad case even at only 784,255 facts.

### Concurrent server behavior

Each user issued a different deliberately broad range. The shared engine used twelve logical workers. Queue
time is included in per-user latency.

| Users | Maximum active | Throughput | p50 | p95/max | Sampled peak working set |
|---:|---:|---:|---:|---:|---:|
| 1 | 1 | 14.20 q/s | 70.24 ms | 70.24 ms | 125.44 MiB |
| 8 | 6 | 21.64 q/s | 212.90 ms | 369.48 ms | 280.02 MiB |
| 32 | 32 | 22.68 q/s | 847.79 ms | 1,381.70/1,410.59 ms | 954.84 MiB |
| 64 | 64 | 16.80 q/s | 2,631.91 ms | 3,733.65/3,810.27 ms | 1,967.48 MiB |
| 64 | 6 | 29.73 q/s | 1,190.99 ms | 2,048.95/2,151.59 ms | 308.59 MiB |
| 64 | 12 | 28.09 q/s | 1,244.27 ms | 2,255.34/2,277.04 ms | 497.38 MiB |

Admission control at six active queries is superior to unlimited or twelve-active execution: it delivered
the highest measured throughput and bounded memory below 309 MiB. It does not make 64 simultaneous broad
folds subsecond; CPU service capacity still creates a roughly two-second p95 queue.

### Backend to browser

The E2E harness materialized the real aggregate values into a dense 2,008,081-element Float32 buffer and
padded marks without results with `NaN`, producing an 8,032,324-byte body. The current experiment does not
yet prove that those values are ordered by production `(tile_key, mki)`; this run isolates the measured
compute, transfer, decode, replacement-buffer, and paint costs.

| Path | Total p50/p95 | Backend p50/p95 | Body p50/p95 | Decode p50/p95 |
|---|---:|---:|---:|---:|
| Localhost through next paint | 179.4/217.0 ms | 129.0/176.2 ms | 19.2/27.4 ms | 0.6/0.7 ms |
| 100 Mbit/s + 20 ms | 831.6/845.8 ms | 122.8/132.7 ms | 683.9/690.3 ms | 0.7/0.7 ms |

The browser used Chromium SwiftShader, so the GPU timing validates buffer replacement mechanics rather than
physical-GPU rendering. After thirteen requests the persistent server used 138.23 MiB working set,
230.47 MiB peak working set, 89.44 MiB private memory, 3.641 CPU-seconds, and 157.68 MiB process reads.

Geometry, tile cover, mark ordering, coloring, picking, and per-mark GPU filters remain unchanged.

## Dataset shape

The disposable Lance dataset must persist request-time identities rather than recalculate them:

```text
fact_id
category_cell_id
timestamp_seconds
tile_or_spatial_identity
mark_identity
LOD mapping identity
measure partials
numeric predicate values used by the fixture
```

The first correctness population is the complete real reference map and frozen leaf/internal tile covers.
The previously reconciled source and viewport counts must be recorded with checksums. Scaling is earned only
after this population produces exact parity.

## Index layouts to compare

### A — unindexed Lance baseline

Use the production-shaped columns and DataFusion aggregation with no scalar index. This separates storage and
execution cost from indexing benefit.

### B — independent scalar indexes

Create:

- a bitmap index on `category_cell_id` or the equivalent low-cardinality fact-side dimensions;
- a B-tree index on `timestamp_seconds`;
- B-tree indexes only for the exact numeric columns included in the frozen query corpus.

Run equality, `IN`, timestamp-range, and combined predicates. Capture the physical plan and index/scan metrics
needed to prove which indexes participated. Do not infer index intersection from latency alone.

### C — category-partitioned Lance fragments

Physically cluster or partition fragments by the existing Cartesian cell code, then order the fact payload by
timestamp and stable mark identity. This tests whether top-level category routing prevents unrelated cell
fragments and timestamp-index pages from being opened.

The important comparison is identical timestamp ranges under:

```text
all cells
one operator
one operator + one quarter
random multi-cell IN lists
```

The experiment passes this part only if category selectivity reduces traversal and physical bytes, not merely
the number of rows returned after a common global traversal.

### D — minimal aggregate-index provider

Run this only if the off-the-shelf Lance paths are exact but broad-range aggregation remains too expensive.
Expose the proven timestamp aggregate artifact through a custom DataFusion `TableProvider` or execution node:

```text
selected Cartesian cell IDs
→ only those cells' canonical timestamp-tree roots
→ complete time nodes + exact boundary facts
→ per-LOD mark partials
→ Arrow RecordBatch in (tile_key, mki) order
```

DataFusion continues to own expression handling, planning, execution, cancellation, and Arrow output. The
specialized code owns only index routing and partial retrieval; it is not a replacement query engine.

## Correctness corpus

Every result must match the current exact fold or a simpler brute-force oracle for:

- inclusive arbitrary-second boundaries;
- empty, single-second, nearly full-day, multi-day, and full-domain ranges;
- one category, several categories, and all categories;
- timestamp plus operator, quarter, and every other active per-fact filter;
- marks with zero, one, and multiple surviving facts;
- leaf marks, merged internal-LOD marks, and parent/child stand-ins;
- configured additive/mergeable measures and `NaN` for no surviving facts;
- map response, clicked-tile inspect, and stale-query cancellation.

Category predicates that are authored as per-mark GPU filters are outside this optimization. Only fact-side
category predicates belong in the backend fold and may reduce index traversal.

## Worst-case performance corpus

Do not benchmark only narrow or cache-friendly filters. Generate deterministic random ranges plus constructed
worst cases:

- a broad timestamp interval with two almost-full boundary partitions;
- a broad interval selecting all category cells;
- a broad interval selecting one category cell;
- disjoint consecutive interactions that minimize reusable pages;
- large numeric ranges with poor selectivity;
- one, eight, and 64 unrelated users, with at most six queries active;
- one active color measure and a separate clicked-tile inspect with all configured inspect measures.

Measure two-core operation for the serving target and six-core saturation for the known host. Report both;
do not mix them in one latency claim.

## Required measurements

### Bake and storage

- Lance conversion time and peak process working set;
- base Lance bytes, each scalar index's bytes, aggregate-provider bytes, and peak temporary build space;
- bytes per fact, mark, category cell, time node, LOD mapping, and configured measure partial;
- incremental append/index-refresh behavior separately from immutable full builds.

### Query and process

- index lookup, row retrieval, aggregation, LOD translation, Arrow encoding, and total backend latency;
- warm, buffered, genuinely cold, and direct-I/O-equivalent states where supported;
- process CPU time, average and peak core utilization, working set, private bytes, allocations, page faults,
  physical/logical read bytes, IOPS, throughput, and disk queue;
- plans and operator metrics proving which category and timestamp index structures were traversed;
- cancellation cost and memory reclamation after completed and abandoned interactions.

### End to end

- request receipt through final response body;
- response headers, body transfer, Arrow decode, value-buffer replacement, and next paint;
- localhost and a declared 100 Mbit/s plus 20 ms network model;
- uncompressed and actual compressed response bytes without relying on favorable compression for acceptance.

## Decision gates

The experiment supports direct Lance + DataFusion serving only if:

1. every exact result matches the oracle at leaf and internal LODs;
2. combined category and timestamp predicates demonstrably avoid unrelated category index/data pages;
3. broad random queries do not degrade into a fact-volume aggregation incompatible with the latency target;
4. observed serving-process residency remains below 5 GB after all relevant indexes have been exercised;
5. cold and concurrent resource behavior is reported separately from warm single-user results;
6. final output uses the existing remote-fold and inspect contracts without transferring facts to the client.

If scalar indexes pass traversal but fail broad aggregation, retain Lance + DataFusion and test only the
minimal aggregate-index provider in layout D. If scalar indexes do not combine or prune as required, do not
add more indexes blindly; use category-partitioned routing and the aggregate provider. If the custom provider
cannot preserve Cartesian context or per-LOD parity within the storage budget, the candidate fails.

## Conclusion

Direct Lance + DataFusion scalar serving is not the billion-fact solution. Its work remains proportional to
the facts selected. The measured cell contains 784,255 facts; the same cell fraction at one billion facts is
roughly 32.3 million facts, before multiple selected cells, numeric conjunctions, or LOD translation. A
linear projection would erase the measured latency margin, and the experiment did not materialize a
billion-row Lance population that could contradict that projection.

Lance and DataFusion should still be retained:

- Lance owns immutable category/time-partitioned data, versioned append/compaction candidates, and selective
  boundary or inspect reads.
- DataFusion owns Arrow expressions, planning, execution, concurrency admission, and the response pipeline.
- A narrow Colossus `TableProvider` or physical operator owns category-sharded canonical timestamp aggregate
  partials, exact boundary facts, and production `(tile_key, mki)` output.
- The planner chooses a contiguous scan for broad ranges and a scalar index only when estimated selectivity
  makes row retrieval cheaper. An index must not be forced merely because it exists.

This is layout D from the experiment plan, now supported by evidence rather than expectation. The next proof
must combine the already measured billion-fact timestamp tree with Lance category routing and the real
per-LOD identity contract. Live append/index refresh, random multi-cell lists, numeric conjunctions, inspect,
cancellation, and internal-LOD parity remain unmeasured.
