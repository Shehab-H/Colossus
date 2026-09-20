# 03 — Leading hybrid architecture

**Status:** Candidate — strongest current design, not approved for implementation.

**Last verified:** 2026-08-23.

**Estimate:** Not revised after the measured Lance + DataFusion baseline; productionization is deliberately
not estimated yet.

## Motivation

No single representation wins every exact query:

- small marks are cheaper to scan than index;
- one narrow range is ideal for an ordered index;
- several narrow ranges benefit from fact-level bitmap intersection;
- a broad range makes a B+tree enumerate most leaves;
- a broad multidimensional box can be accepted or rejected efficiently by an aggregate BVH;
- `ALL` needs only mark extrema;
- unique concurrent filters need shared traversal or more CPU, not result caching.

The candidate architecture makes those cases explicit behind one exact planner.

## Immutable serving artifacts

The runtime never contacts the source database. A map version owns immutable artifacts partitioned first by
map and spatial tile/LOD context.

### Fact store

Store each exact fact once where possible:

```text
fact_id
high-resolution integer spatial code
real geometry/mark ordinal
zreal
four numeric predicate values
categorical/temporal codes or base-cell reference
additive aggregate partials
```

Use compact fixed-width types and structure-of-arrays layout. `fact_id` is the authority used when separate
range indexes are intersected.

### Mark directory

For each rendered tile:

```text
tile_key
mark_code
mki
fact count
four-dimensional root min/max
optional tree root
```

`mki` exactly matches the immutable render-tile row order.

### Ordered numeric range indexes

For each numeric field and spatial partition, store an immutable sorted sequence:

```text
(numeric_value, fact_id)
```

Because versions are immutable, a dynamic transaction-oriented B+tree is not required. A compact sorted leaf
array plus sparse fence pointers can provide the same binary-seek/sequential-range behavior with less
metadata. Lance bitmap and B-tree scalar indexes were tested in item 05. A global high-cardinality B-tree
created hundreds of thousands of random row ranges and lost to a scan. Retain ordered indexes only inside
physically routed partitions and only when the cost model predicts bounded contiguous retrieval.

### Adaptive aggregate BVH

For heavy rendered marks, store a static multidimensional tree. Each node contains:

- minimum and maximum for every predicate field;
- descendant fact range or child offsets;
- additive aggregate partials for all descendants;
- count and any bounded category partials required by measures.

Tiny marks remain inline. The measured provisional threshold was 64 facts and leaf size 64; neither is a
product constant until disk/aggregate benchmarks choose them.

## Exact query planner

For the selected, currently drawable LOD tiles:

```text
1. Compile inclusive ranges and categorical/temporal context.
2. Apply mark root headers.
3. Estimate each numeric posting range from index lower/upper ranks.
4. Choose the least-cost exact plan.
5. Produce surviving facts or node partials.
6. Translate to tile/mki and reduce configured measures.
7. Return compact per-tile results.
```

### Plan A — `ALL` from headers

A mark passes exactly when every mark minimum/maximum is contained by every active range. No facts or lower
tree nodes are needed.

### Plan B — tiny-mark direct scan

When mark facts are already contiguous and few, compare them directly. The current real map has at most 32
facts per leaf mark, and this path achieved the best result without any tree.

### Plan C — one selective ordered driver

Ask all four indexes for range cardinalities, choose the smallest, and validate the other three values for
those candidate `fact_id`s.

This avoids reading four posting ranges when one range is already strongly selective.

### Plan D — multiple posting intersections

If two or more ranges are individually moderate but jointly selective, materialize/stream their exact fact-ID
postings into Roaring-style bitmaps and intersect them. Validate remaining predicates on the intersection.

Always intersect facts first. A mark bitmap intersection is logically invalid.

### Plan E — aggregate BVH

For a node/query box:

```text
node disjoint from query  → contribute nothing
node contained by query   → contribute stored partials
partial intersection      → descend
leaf                      → compare exact facts
```

Correctness follows from bounding-box containment and the distributive partial contract. The structure is
especially useful for heavy marks and ranges that are selective only through the conjunction rather than one
single field.

### Plan F — compact columnar scan

If the visible population is small or every index would enumerate most rows, scan the partition once using
vectorized comparisons. This is a legitimate cost-plan winner for the current 7.25M-fact warm viewport, not
a universal fallback for 100M–1B active facts under concurrency.

### Plan G — complement from a base aggregate

Where the existing slab/base context already provides exact per-mark totals `A(B)`, a very broad numeric
filter may be cheaper as:

```text
A(B ∩ P) = A(B) − A(B ∩ NOT P)
```

This is exact only under the declared arithmetic contract and when the excluded correction set is cheap to
identify and reduce. It remains a candidate optimization, not a proven default.

## Cost model

The planner should estimate:

- visible facts and marks;
- per-index candidate counts from lower/upper ranks;
- expected fact gathers and disk pages;
- BVH root/node residency and predicted partial nodes;
- existing hot pages/tiles;
- output mark count and bytes;
- current CPU queue depth;
- whether the interaction is a slider delta from the previous range.

The cost unit should be estimated bytes/pages plus CPU work, not an abstract row count alone.

## Slider-delta optimization

Within one user session, slider boundaries move continuously. Retain the previous exact fact bitmap and
per-mark partial state. A sorted range index identifies the row-ID slice crossing the changed boundary;
only those facts are added or removed after checking the other predicates.

This can make repeated interaction proportional to the boundary delta, but it does not improve a cold first
query or unrelated users. Versioned cancellation is required so stale deltas cannot overwrite newer state.

## Source independence and sharing

- Artifacts are created during bake from any source adapter.
- One immutable mapped representation is shared by every user in a process.
- Per-user state is ranges, small traversal stacks, a result bitmap, and optional aggregate output buffers.
- Multiple worker processes should map the same file-backed pages rather than privately deserialize complete
  indexes.
- Result caching is opportunistic; correctness/performance cannot depend on query overlap.

## Output contract

Prefer:

- visibility: one bit per mark over the network;
- discrete visual encoding: UInt8/UInt16 palette or category code;
- continuous exact value: Float32 only when required by the active render measure;
- full inspection measures: fetch on click for one tile/mark where product semantics permit it.

The renderer should update a per-tile visibility/value texture indexed by static `mki`, rather than expanding
a changed visibility flag across every polygon vertex.

## Integration surface

- Current slab and measure fold:
  [`slab.ts`](../../../web/src/features/map-viewer/engine/slab.ts) and
  [`measures.ts`](../../../web/src/features/map-viewer/engine/measures.ts).
- Bake-time mark identity:
  [`MarkKey.cs`](../../../src/Colossus.Infrastructure/Tiling/MarkKey.cs).
- Current remote seam:
  [`remoteFold.ts`](../../../web/src/features/map-viewer/engine/remoteFold.ts).
- Current tile cache and immutable identity:
  [`tileCache.ts`](../../../web/src/features/map-viewer/engine/tileCache.ts).

## Risks

- Four ordered indexes add roughly eight raw bytes per fact per dimension before metadata when represented as
  Float32 plus 32-bit local row ID: about 3.2 GB per 100M facts, in addition to payloads.
- Random fact gathers after a selective index scan can cause more page faults than a sequential scan.
- BVH node aggregates increase index size and introduce floating-point association questions.
- A planner can be correct but unstable around cost boundaries; plan hysteresis may be needed during sliders.
- Storing facts once conflicts with fast membership at every LOD unless the integer spatial/mark mapping is
  designed carefully.
- One billion active facts cannot fit the current warm representation in 5 GB; spatial partitioning is
  mandatory.

## Acceptance evidence

- Every plan matches the same brute-force `(tile_key,mki)` result and aggregate fixtures.
- The planner chooses the expected path for narrow, broad, correlated, anti-correlated, and adversarial boxes.
- Disk bytes/pages and resident memory are reported for each plan.
- A broad query demonstrates contained-node aggregate use rather than merely another leaf scan.
- A selective query demonstrates a bounded posting read and fact gather.
- The current real tiny-mark distribution demonstrates that no unnecessary trees are built.

## Next action

Do not implement the complete hybrid first. [Item 05](05-production-shaped-lance-datafusion-proof.md)
established the Lance + DataFusion baseline: global scalar indexes failed, while category routing and
timestamp ordering succeeded but remained fact-volume-bound. Build only the missing category-sharded
aggregate provider, then test it against persisted leaf and internal-LOD identities.
