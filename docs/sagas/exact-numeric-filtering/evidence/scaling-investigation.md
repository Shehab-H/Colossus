# Colossus numeric-filter scaling investigation

> **R&D status — 2026-08-21:** The client-side sharded GPU design described in this
> investigation was implemented as a 100-million-fact benchmark. GPU filtering was fast, but
> generic cold queries required hundreds of megabytes of transfer and up to gigabytes of client
> working memory. It is therefore **rejected as the general solution**. Primary-field sharding is
> retained only as a possible optimization for a deliberately restricted filter contract. See
> [the GPU benchmark](gpu-resident-benchmark.md) and
> [the sharding benchmark](client-sharding-benchmark.md).

## Context

Colossus renders distinct map marks while retaining multiple fact rows behind each mark. Active filters
select facts, and every displayed measure is recomputed over the surviving facts for each mark. A mark with
no surviving facts disappears. Geometry, visibility, color, inspection values, and picking must remain
exact. Approximate results, sampled marks, visual aggregation, and bucketed rendering are not acceptable.

The current Cartesian companion/slab model works well for bounded categorical and temporal axes and is a
valuable part of the system. The scaling failure appears when high-cardinality numeric measurements are
treated as Cartesian axes rather than as predicates over facts.

The reference `mobile_coverage` dataset contains 24,257,354 facts and 2,008,081 distinct marks. Its measured
cardinalities are:

| Field | Distinct values |
|---|---:|
| `operator` | 4 |
| `quarter` | 8 |
| `tests` | 2,463 |
| `sample_share` | 11,415,983 |
| `download_mbps` | 18,411,577 |
| `upload_mbps` | 15,417,872 |
| `latency_ms` | 4,180,160 |

The four high-cardinality measurements are `Float32`. Multiplying all domains produces a theoretical cell
space near 10^33. Numeric cardinality grows roughly with the number of facts, so the Cartesian representation
becomes progressively worse as datasets grow.

The expected operating range is approximately 300 million facts initially, potentially more than one
billion facts, with many concurrent interactive users.

## Exact filter semantics

Filtering cannot end with a set of matching row IDs. Surviving facts must be folded back into exact values
for every affected mark. Representative measures are:

```text
total_tests  = sum(tests)
avg_download = sum(download_mbps * tests) / sum(tests)
avg_upload   = sum(upload_mbps * tests) / sum(tests)
avg_latency  = sum(latency_ms * tests) / sum(tests)
avg_share    = sum(sample_share) / count()
```

The useful stored quantities are therefore additive partials: counts, weights, weighted sums, and ordinary
sums. Final averages are derived after filtering. Any numeric-filter structure that only finds rows but does
not address exact per-mark reduction solves only part of the problem.

## Failure of the current numeric-axis representation

The bake currently discovers complete ordered domains for companion axes and encodes a mixed-radix cell ID.
This is appropriate when domains are small. It is unsuitable for nearly unique floating-point measurements:

- Domain discovery retains and orders millions of values.
- Cell count is the product of domain cardinalities rather than the number of observed facts.
- Dense layout is impossible, while sparse layout still inherits enormous domain metadata and cell-ID
  arithmetic.
- The client cannot usefully consume millions of slider values; numeric controls need bounds and distribution
  summaries rather than a distinct-value list.

The map semantics do not require numeric measurements to be Cartesian axes. They require exact predicates and
exact partial aggregation.

## Measurements

Measurements were taken on the actual 24,257,354-row dataset on an Intel i5-12400F with six physical cores,
twelve logical processors, and 32 GiB RAM.

### ClickHouse scan baseline

A representative broad conjunction over all four numeric measurements selected 16,577,078 facts and left
1,673,963 surviving marks.

| Operation | Measured result |
|---|---:|
| Predicate scan | approximately 260 ms |
| Predicate plus exact per-mark color | approximately 660–730 ms |
| CPU time for the complete grouped query | approximately 1,416 ms |
| Query-local memory | approximately 144 MiB |
| Logical decoded input | approximately 995 MiB |
| Compressed/page-cache reads | approximately 427 MiB |

The files were warm, so physical disk reads were zero. The query nevertheless reread and decoded the cached
column data. Concurrency degraded sharply: two simultaneous queries took approximately 1.4 seconds each,
four took approximately 4 seconds, and eight took approximately 11 seconds.

This path avoids the bake failure but does not provide adequate concurrency on one node.

### Packed resident-column baseline

A disposable exact prototype loaded a row-aligned serving representation containing:

```text
mark_id
tests
sample_share
download_mbps
upload_mbps
latency_ms
```

It evaluated the four numeric ranges with AVX2, produced a fact-selection bitmap, and recomputed exact
weighted color values by mark.

| Operation | Measured result |
|---|---:|
| Serving payload | 555 MiB |
| Actual resident working set | 652 MiB |
| Exact four-range predicate | 15–17 ms |
| Predicate plus one exact weighted color | 75–80 ms |
| Predicate plus all five values | approximately 175–210 ms |
| Scratch space per active query | approximately 41 MiB |
| Disk I/O per query | zero |

Eight concurrent queries completed in approximately 144 ms while using about 1 GiB total working set and
60% of the host CPU. Thirty-two concurrent queries completed in approximately 600 ms while using about
2.3 GiB and 71% CPU.

This result is a useful lower-complexity performance baseline, not an index result. Every query scanned all
four resident numeric columns. At 300 million facts the resident representation would be roughly 6.7 GiB,
and a broad query would approach one second on the same single machine before network and client work.

### Compression

The high-cardinality Float32 values were effectively incompressible in the source ordering:

| Column | Uncompressed | Compressed |
|---|---:|---:|
| `sample_share` | 92.53 MiB | 92.93 MiB |
| `download_mbps` | 92.53 MiB | 92.93 MiB |
| `upload_mbps` | 92.53 MiB | 92.93 MiB |
| `latency_ms` | 92.53 MiB | 91.35 MiB |

The four columns occupy approximately 370 MiB either way. Storage and transfer planning cannot depend on a
large compression gain for these values.

## Investigation of independent indexes

A sorted index for one numeric column can store `(value, row_id)` in value order. A range lookup finds its
boundaries in logarithmic time and reads only the row IDs inside the range. This is effective when the range
is selective. Four independent indexes can be intersected, starting from the smallest estimated posting
range.

That design has two limitations. First, a broad range returns most row IDs and degenerates toward a scan.
Second, after finding row IDs, the system still has to fetch their additive partials and reduce them by mark.
Random fact retrieval can dominate unless rows and postings are partitioned and ordered carefully.

Bit-sliced and bitmap representations eliminate value-domain enumeration, but exact broad comparisons still
stream bit planes over the candidate population. They exchange a value scan for a bitmap scan and may be
useful for Boolean intersection, but they do not by themselves eliminate broad-query work or per-mark
reduction.

A single global index also ignores useful structure already present in Colossus: spatial tiles, local mark
IDs, categorical cells, temporal ordering, and additive measure partials.

## Factorized inclusion/complement design

The strongest direction found so far preserves the current cube and treats numeric filtering as an exact
correction around its result.

Let `B` be the fact set selected by the existing categorical and temporal cube. The cube already provides the
per-mark additive aggregate `A(B)`. Let `P` be the conjunction of active numeric range predicates.

The desired result is:

```text
I = B intersect P
A(I)
```

There are two exact ways to compute it.

### Inclusion path

The numeric indexes enumerate facts inside the numeric predicates. The smallest posting range is used as the
candidate set, the other predicates and current cube membership are validated, and surviving fact partials
are accumulated from zero:

```text
A(I) = aggregate(included facts)
```

This path is efficient for selective numeric filters.

### Complement path

For broad ranges, the numeric indexes enumerate facts outside the predicates. The excluded set is:

```text
E = B intersect (not P)
  = B intersect (outside P1 union outside P2 union ... outside Pn)
```

Excluded row IDs are deduplicated, validated against `B`, and their additive partials are subtracted from the
existing cube result:

```text
A(I) = A(B) - A(E)
```

This path makes a 95%-inclusive numeric filter proportional to the excluded 5% rather than the included 95%.
It also gives the existing cube a direct role instead of replacing it.

The query planner can estimate both sides from numeric-index ranks and cube occupancy, then choose the
smaller amount of exact fact work. Selective queries aggregate inclusions; broad queries subtract exclusions.
The worst region is where included and excluded populations are both large, but the design avoids the common
failure in which broad filters automatically become full scans.

Conjunctions require care. The complement of a conjunction is a union, so a fact outside several ranges must
be subtracted exactly once. A tile-local bitmap, generation-stamped row array, or sorted row-ID union can
provide exact deduplication.

## Physical organization

The factorized representation is naturally local to the existing immutable spatial tiles or storage shards.
Each shard can contain:

- local `mark_id` per fact;
- the existing bounded cube-cell membership;
- exact numeric values or ordered numeric indexes;
- the additive partial vector required by configured measures;
- row counts and rank statistics for query planning.

Local row IDs are smaller and more compressible than global IDs. Spatial and categorical pruning happens
before numeric work. Posting slices can be memory-mapped and read on demand rather than loading every index
for every dataset into RAM.

Facts can remain grouped by mark in the payload while each numeric index stores a permutation or posting list
of local fact IDs. Candidate IDs are sorted or block-grouped before partial retrieval, converting random
access into mostly sequential page reads.

The same immutable shard format could support either server execution or client execution. A server can keep
hot index pages resident and return aligned mark-value planes. A capable client could receive the exact fact
sidecar only for loaded map tiles and perform predicate evaluation plus segmented reduction with WASM SIMD or
WebGPU. Client execution transfers work away from shared server concurrency and makes immutable artifacts
CDN-cacheable, but its viability depends on visible-tile fact counts, device memory, browser support, and
first-load byte budgets.

## Incremental interaction

Slider movement changes range boundaries rather than defining an unrelated query every frame. A session can
retain its selected-row bitmap and per-mark additive state. When a boundary moves, the sorted index identifies
the exact row-ID slice crossing that boundary. Only those facts are added to or removed from the maintained
aggregate, after validation against the other predicates.

This changes continuous interaction from repeated complete range evaluation into exact delta application.
The first query still needs an inclusion or complement plan, while subsequent nearby slider positions can be
proportional to the boundary delta. Cancellation and query-version checks are still required when movements
arrive faster than processing.

## Storage and complexity

The representation is linear rather than Cartesian. A straightforward sorted index containing a Float32
value and 32-bit local row ID costs eight bytes per fact per numeric column before metadata. Four such indexes
would cost approximately 32 bytes per fact, or 9.6 GB for 300 million facts, in addition to partial payloads.
Tile-local IDs, delta coding, block indexes, wavelet-style layouts, and storing values only once can reduce
that cost, but the actual data must be measured because the Float32 values themselves showed almost no
compression.

The inclusion/complement query cost is approximately:

```text
index boundary lookup
+ candidate row-ID reads
+ Boolean validation/deduplication
+ partial retrieval
+ reduction of the smaller exact correction set
+ output proportional to affected marks
```

The candidate term is normally closer to the smaller of the included and excluded populations than to the
entire fact population. Pathological mid-selectivity multidimensional predicates can still approach linear
work. Returning an updated value for every visible mark also has an unavoidable output cost proportional to
the number of marks whose displayed state changes.

## Numerical exactness

The complement path relies on subtracting additive partials. Floating-point addition is not associative, so
subtracting from a precomputed total may not be bit-for-bit identical to folding included facts in a different
order. Exact visual and inspection parity requires a canonical arithmetic contract.

Possible contracts include fixed-point integer partials where source precision permits it, wider deterministic
accumulators, or a defined stable summation order shared by bake and runtime. Counts and integer weights are
naturally exact. Weighted Float32 measurements require deliberate treatment rather than assuming ordinary
floating-point subtraction is identical to a fresh fold.

## Pre-benchmark conclusion (superseded)

The numeric measurements should not be additional Cartesian axes, and repeated Parquet or OLAP scans are not
an adequate concurrency strategy. A complete resident-column scan is surprisingly fast at 24 million facts,
but it is a baseline rather than the desired index design.

The most promising architecture is a factorized hybrid that keeps the current categorical/temporal cube as
the authoritative base aggregate, stores exact numeric indexes and additive fact partials beside each spatial
shard, and chooses between aggregating included facts and subtracting excluded facts. This preserves the
current map model, makes both selective and broad ranges exploitable, keeps storage linear, and permits exact
incremental updates as slider boundaries move.

The remaining uncertainty is not whether the Cartesian numeric product can be removed; it can. The hard part
is selecting the concrete shard-local index layout that provides the best balance among disk size, page reads,
candidate intersection, deterministic arithmetic, client transfer, and concurrent execution. The previous
measurements establish realistic baselines for those tradeoffs but do not yet validate a final index layout.

## 100-million-fact sharding result

The uncertainty above was tested with 99,997,497 production-derived facts, four numeric predicates, two
aggregate partials, 100 synthetic output marks, 16,384-fact base shards, and a two-core / 5 GB build limit.
Two immutable client-side layouts were built: a generic four-dimensional Morton layout and a layout sorted by
one configured primary numeric field inside every mark.

The exact tri-state algorithm was correct:

- **OUT** shards contributed nothing.
- **IN** shards contributed their stored aggregate partials.
- **PARTIAL** shards were downloaded and filtered fact by fact.
- Every tested result matched a full scan for every mark.

The problem was not correctness or GPU arithmetic. It was the amount of ambiguous information that still had
to reach the client.

| Cold query on the primary layout | Network | Client raw working set, 2 partials |
|---|---:|---:|
| Primary field, middle 20% | 9.82 MB | 39.3 MB |
| Off-primary field, middle 20% | 550.73 MB | 1.20 GB |
| Four fields, middle 20% each | 220.66 MB | 511.2 MB |
| Four fields, middle 50% each | 525.68 MB | 1.219 GB |

The aligned case was excellent because a sorted interval has only two boundary shards per mark. The same
metadata could not classify arbitrary off-primary or multidimensional ranges, so nearly all shards became
PARTIAL. Six aggregate partials would raise representative working sets to approximately 92 MB, 852 MB,
2.0 GB, and 2.8 GB respectively. At 6,000 simultaneous cold clients, the poor cases imply roughly 1.3–3.3 TB
of CDN egress for one interaction.

### Decision

Do not continue toward a general system that sends exact fact-level predicate and aggregate planes to every
browser. WebGPU proved that the final comparison kernel is cheap once bytes are resident; it did not solve
the dominant transfer, decode, memory, or cache-fill costs.

Keep these pieces as bounded optimizations:

- primary-field ordering when a product explicitly restricts filtering to that field;
- min/max tri-state pruning inside already-small visible/context tiles;
- immutable CDN objects and local caching;
- GPU filtering of data already loaded for rendering;
- additive partials and an explicit deterministic arithmetic contract.

## What CARTO actually does

CARTO does not appear to solve arbitrary, exact, billion-row filtering by moving the full fact index into the
browser. Its documented architecture separates several jobs:

1. **Progressive spatial delivery.** Maps load vector tiles for the current viewport and zoom instead of the
   whole table. Dynamic tiles are generated by SQL pushed into the connected cloud data warehouse.
2. **Level of detail.** Geometries are simplified and features may be discarded at low zoom. Very large or
   complex datasets are converted into pre-generated tilesets.
3. **Spatial aggregation.** H3 or Quadbin tile pyramids can store precomputed counts, sums, and other formulas
   at multiple resolutions.
4. **Exact analytic filtering.** Table and query sources apply column filters server-side using the cloud data
   warehouse. Exact widgets also prefer server-side SQL calculations.
5. **Static/client filtering.** Pre-generated tilesets can be filtered on the client, including with deck.gl's
   GPU DataFilterExtension, but calculations are limited to data in loaded tiles. CARTO documents that
   off-screen/unloaded tiles are omitted from client-side widget calculations.
6. **Shared caching.** Query and tile results are cached in a CARTO-managed CDN so identical requests do not
   repeatedly reach the warehouse.

Official references:

- [CARTO performance considerations](https://docs.carto.com/carto-user-manual/maps/performance-considerations)
- [CARTO column filters](https://docs.carto.com/carto-for-developers/reference/filters/column-filters)
- [CARTO server-side versus client-side widgets](https://docs.carto.com/carto-for-developers/reference/carto-widgets-reference/server-side-vs.-client-side)
- [CARTO cache management](https://docs.carto.com/carto-for-developers/guides/managing-cache-in-your-carto-applications)
- [deck.gl DataFilterExtension](https://deck.gl/docs/api-reference/extensions/data-filter-extension)

The architectural lesson is that CARTO changes the problem rather than defeating its information cost. The
map is viewport-local and resolution-dependent; exact global analytics go to a warehouse; repeated results
are cached; and the GPU only operates on data that has already arrived.

## Constraint exposed by the experiments

For arbitrary combinations of exact numeric ranges, the system must obtain enough information to distinguish
facts just inside a range from facts just outside it and then assign their partials to the correct marks. That
information must exist in at least one of three places:

1. row-level or sufficiently fine-grained data transferred to the client;
2. a runtime query engine that reads an index or facts near the data; or
3. a precomputed answer space whose supported filters are restricted and finite.

Sharding changes the amount read when its partitions align with a query, but it does not create a fourth
location for the missing information. With arbitrary filters, exact results, no runtime compute, and small
client transfers all required simultaneously, the requirements conflict.

## Next R&D direction

The next design should be a **dual-plane system**, following the useful part of the CARTO pattern while
respecting Colossus semantics:

### Map plane

- Pre-generated spatial tiles or H3/Quadbin pyramids for geometry and zoom-appropriate display aggregates.
- CDN delivery, aggressive caching, progressive refinement, and GPU filtering only within resident tiles.
- A clear distinction between a rendered overview and exact fact-level inspection.

### Truth plane

Test two alternatives rather than assuming one architecture:

1. **Near-data exact query engine.** Push filter and per-mark reduction into a columnar engine beside the data;
   cache results by dataset version plus canonical filter. The two-core application server routes and
   authenticates but does not scan. This is the pattern closest to CARTO.
2. **Restricted compiled filter contract.** If runtime query compute is prohibited, declare the supported
   filter dimensions, combinations, resolution, and ANY/ALL semantics during configuration, then bake a
   compact multidimensional range-aggregate index or cube. Measure its growth before accepting the product
   restriction. It can be exact only for the declared contract.

The next benchmark should compare those two truth planes on the same 100-million-fact corpus. Measure cold and
warm latency, bytes read near storage, result bytes, peak RAM, CPU-seconds, cache hit behavior, and throughput
under concurrent distinct filters. Do not spend another benchmark on GPU kernel speed; that part is already
fast enough.

## Adaptive mark-index benchmark — 2026-08-21

The next index experiment tested an exact, source-independent **adaptive mark BVH** rather than another
generic database scan. Facts were stored contiguously by mark. Every mark stored one four-dimensional
min/max header; marks with more than 64 facts additionally received a static median-split k-d/BVH tree with
64-fact leaves and a four-dimensional bounding box at every node. `ANY` queries used the following exact
tri-state traversal:

- disjoint node → reject;
- node wholly inside the query → accept the mark immediately;
- partial node → descend, with exact fact comparisons at leaves;
- small mark → scan its contiguous facts directly.

`ALL` needs only the mark header: every fact passes exactly when the mark's minimum and maximum are contained
by the requested range on every active dimension. Every `ANY` result bitmap was byte-compared with the exact
flat path. All comparisons passed.

### Method and scope

- Release .NET 10 prototype, process affinity pinned to two logical processors (`0x3`) on an Intel i5-12400F.
- Four exact Float32 range dimensions. The production viewport used `download_mbps`, `upload_mbps`,
  `latency_ms`, and `sample_share` from the current baked facts Parquet.
- Output was a dense visibility bitmap, including its clear/write cost in every timing.
- Query families used random locations with fixed quantile widths: one-dimensional middle 20%, and
  four-dimensional 80%, 50%, 10%, and 1% windows.
- Warm resident filtering was measured separately from artifact loading and index construction.
- The production run used 160 distinct queries per family. The 100-million-fact stress runs used 60.
- Synthetic stress data was generated in memory and released when each process exited; no synthetic dataset
  was written to disk.

### Current production viewport

The pinned 12-tile Europe viewport in the current bake contained **7,250,351 facts and 583,511 marks**. The
maximum was only 32 facts per mark, so the adaptive policy correctly built **zero trees**. The useful index for
this distribution is simply the compact mark header plus a contiguous scan for partial marks.

| Exact identification, two processors | p50 | p95 | p99 |
|---|---:|---:|---:|
| `ANY`, one numeric dimension, middle 20% | 8.71 ms | 11.07 ms | 14.67 ms |
| `ANY`, four dimensions, broad 80% | 11.62 ms | 13.40 ms | 14.55 ms |
| `ANY`, four dimensions, middle 50% | 17.42 ms | 20.51 ms | 23.16 ms |
| `ANY`, four dimensions, narrow 10% | 9.63 ms | 14.26 ms | 17.92 ms |
| `ANY`, four dimensions, needle 1% | 5.87 ms | 9.87 ms | 11.65 ms |
| `ALL`, four dimensions, middle 50% | 2.73 ms | 4.03 ms | 5.29 ms |

The modeled resident footprint for facts, mark offsets, headers, roots, nodes, and the output bitmap was
**132.9 MB**. Reading, sorting, and materializing the viewport from the existing general Parquet took 3.50–3.63
seconds; that is a cold preparation result, not part of the warm filter numbers. A production serving artifact
must be baked directly into mark-contiguous/index order and memory-mapped rather than sorted on activation.

### Heavy-mark stress results

The first adversarial corpus held 24 million facts and 583,511 marks. Five thousand heavy marks contained an
average of 4,337 facts whose values were uniform across the complete four-dimensional domain. This prevents
the per-mark header from pruning most selective queries and forces the tree itself to prove its value.

| 24M facts / 5,000 uniform heavy marks, two processors | Flat p99 | BVH p99 |
|---|---:|---:|
| Four dimensions, broad 80% | 21.37 ms | 25.38 ms |
| Four dimensions, middle 50% | 31.11 ms | 21.59 ms |
| Four dimensions, narrow 10% | 63.41 ms | 22.80 ms |
| Four dimensions, needle 1% | 67.41 ms | 15.28 ms |

Broad ranges often find a matching fact immediately, so the tree can be slightly slower there. Selective
ranges expose the flat scan's linear tail and show the tree's benefit. The modeled indexed footprint was
446.8 MB and all result bitmaps matched exactly.

A separate 24-million-fact run placed approximately **902,753 facts in each of 24 heavy marks**. The BVH's
two-processor p99 was 23.19 ms for middle-50%, 10.27 ms for narrow-10%, and 7.72 ms for needle-1% ranges.
This directly tests the large-fact-count-per-mark case: tree depth, not raw facts per mark, dominated the
selective-query cost.

### 100-million-active-fact boundary test

The stronger stress corpus contained 100 million active facts and the same 583,511 output marks. With 5,000
uniform heavy marks, the tree contained 5,115,000 nodes, built in 19.24 seconds, and the complete uncompressed
prototype representation modeled **1.782 GB**. It stayed well below the 5 GB target without compression.

| 100M facts / 5,000 uniform heavy marks, two processors | Flat p99 | BVH p99 |
|---|---:|---:|
| One dimension, middle 20% | 30.95 ms | 21.83 ms |
| Four dimensions, broad 80% | 15.63 ms | 16.17 ms |
| Four dimensions, middle 50% | 55.07 ms | 25.50 ms |
| Four dimensions, narrow 10% | 180.85 ms | 18.60 ms |
| Four dimensions, needle 1% | 162.21 ms | 11.65 ms |

A second 100-million-fact run used only 24 heavy marks, approximately 4.07 million facts each. Its BVH p99
remained 21.02 ms for middle-50%, 10.42 ms for narrow-10%, and 8.15 ms for needle-1% ranges. The modeled
footprint was 1.692 GB. All 100-million-fact parity comparisons passed.

### Concurrency result

The index meets the sub-50-ms goal for one warm query on the tested distributions, but the two-core machine
does **not** meet it for 100 unrelated queries submitted at once. On the real viewport, 100 exact middle-50%
four-dimensional queries completed with:

| 100-query burst | Result |
|---|---:|
| response p50 | 1,011 ms |
| response p95 | 1,640 ms |
| response p99 | 1,646 ms |
| burst wall time | 1,650 ms |
| throughput | 60.6 queries/s |

Oversubscribed per-query service time also rose to 314 ms p50 / 474 ms p95. This is the CPU queueing limit,
not an index-correctness failure. Sixty maps times 100 concurrent users cannot receive isolated sub-50-ms
execution from one two-core process. The next experiment must batch unique filters and traverse the same
mark headers/tree nodes once with active-query bit masks, then compare that shared traversal with horizontal
replication.

### Updated decision

The adaptive mark-local index is now the leading exact design:

1. bake facts in spatial-tile then mark-contiguous order;
2. always store one four-dimensional min/max header per mark;
3. scan small marks directly;
4. build aggregate BVH nodes only above a measured fact-count threshold;
5. keep the visibility answer as a bitset;
6. add stored additive partials to nodes in the next benchmark for exact aggregate recomputation;
7. memory-map spatial partitions and keep only active tiles/top levels resident;
8. batch concurrent distinct filters rather than launching 100 independent traversals.

The experiment validates sub-50-ms **warm mark identification through 100 million active facts** for the
tested real, clustered, uniform, selective, and broad query families. It does not prove a worst-case bound
for every possible distribution or a one-billion-active-fact viewport. Linear extrapolation of the current
uncompressed 100-million-fact representation is about 17–18 GB at one billion facts, so a 5 GB server must
partition and page the index; sub-50-ms cold access cannot be claimed until that memory-mapped layout is
measured.
