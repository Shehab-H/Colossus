# Exact numeric-filter sharding benchmark

> **R&D disposition — 2026-08-21:** Correctness passed, but this design is rejected as
> the general architecture for arbitrary numeric filters. The favorable result applies only when
> the query aligns with a configured primary ordering or when earlier spatial/context pruning has
> already made the fact population small.

## Benchmark verdict

The technique passes exactness and is useful in one aligned case, but fails as a general solution to
arbitrary numeric filtering.

- A generic four-dimensional Morton/Z-order layout saved bytes, but cold queries still needed roughly 259–801 MB.
- A layout sorted by a configured **primary numeric filter inside each output mark** reduced a middle-range query over 100 million facts to **9.82 MB total cold wire usage**.
- Every tested hybrid result matched the full scan exactly for every mark, including selected-count hashes and aggregate totals.
- Arbitrary off-primary filters and broad multi-filter queries still needed 220–551 MB. Sharding alone cannot make an unaligned exact query cheap.

If this technique is used as a restricted optimization, it should be:

1. Spatial/context/LOD shards first, to limit the active working set.
2. Primary-numeric ordering inside each context and output mark.
3. Exact min/max tri-state classification.
4. Separately fetchable predicate and aggregate planes.
5. A byte-cost planner that refuses the client path when the predicted payload is too large.

In that restricted use, execution remains client-side: the backend serves immutable objects and manifests;
it does not execute user filters.

## Physical test

- Dataset: 99,997,497 production-derived [ClickBench](https://github.com/ClickHouse/ClickBench) events.
- Synthetic output layout: 100 output marks, approximately one million facts per mark.
- Four numeric predicates and two aggregate partials.
- Atom width: 24 raw bytes.
- Base shard size: 16,384 facts.
- Base shards: 6,200.
- Exact metadata per shard: output mark, fact count, four min/max pairs, count/numerator/denominator totals, and plane byte offsets.
- Query-building limit: two CPU threads and 5 GB RAM.

The facts and numeric distributions are real. Output-mark assignment is round-robin and synthetic so that every mark receives approximately the full data distribution. This intentionally makes the test difficult. Results will differ when real marks correlate with the filter values.

## Layouts tested

### Generic Morton layout

Facts were normalized and sorted by a four-dimensional Morton/Z-order inside each mark.

- Compressed atom pack: 1.144 GB.
- Compressed manifest: 617.5 KB.
- Advantage: reasonable pruning across several arbitrary numeric dimensions.
- Weakness: a single-field range still leaves many multidimensional shard bounding boxes ambiguous.

### Primary-filter layout

Facts were sorted by `event_second_of_week` inside each mark.

- Compressed atom pack: 1.039 GB.
- Compressed manifest: 618.6 KB.
- Advantage: a range on the primary field has at most two partial boundary shards per mark. Interior shards are exact IN or OUT decisions.
- Weakness: a range on another field receives almost no shard pruning.

The earlier monolithic Parquet/Zstandard pack was 880.6 MB. Random-access frames therefore add an 18% storage penalty for the primary layout and a 30% penalty for Morton. That is the cost of independent decoding and range fetching.

## Exactness proof by execution

For each tested query, the benchmark did both:

1. Classify shards as IN, OUT, or PARTIAL, use stored totals for IN, and decompress/filter only PARTIAL shards.
2. Run a complete two-thread scan over all 99,997,497 facts.

Selected counts and per-mark hashes matched exactly for all 100 marks. Numerator and denominator totals also matched with zero measured error.

Representative selected totals:

| Query | Selected facts |
|---|---:|
| Primary middle 20% | 20,009,976 |
| Client-IP middle 20% | 19,949,624 |
| URL-hash middle 20% | 19,946,610 |
| Resolution middle range | 48,985,785 |
| Four middle-20% ranges | 408,456 |
| Four middle-50% ranges | 7,214,723 |

## Full cold-network usage: recommended primary layout

The table includes:

- Zstandard-compressed plane bytes.
- A 1 KiB estimate per HTTP range response.
- The 618.6 KB compressed manifest, fetched once.
- Exact contiguous runs in one packfile per plane.

It does not include TLS handshakes, TCP slow-start, packet framing, retries, or CDN-provider-specific overhead. Repeated queries can be zero-network when all required ranges are cached.

| Query | IN / OUT / PARTIAL facts | Requests including manifest | Cold wire | 100 Mbit/s floor | 1 Gbit/s floor | 6,000-client egress |
|---|---:|---:|---:|---:|---:|---:|
| Include all | 100.0M / 0 / 0 | 1 | 0.62 MB | 0.05 s | 0.005 s | 3.7 GB |
| Reject all | 0 / 100.0M / 0 | 1 | 0.62 MB | 0.05 s | 0.005 s | 3.7 GB |
| Primary middle 20% | 18.0M / 78.7M / 3.28M | 601 | **9.82 MB** | **0.79 s** | **0.08 s** | **58.9 GB** |
| Primary middle 80% | 77.0M / 19.7M / 3.28M | 601 | 11.15 MB | 0.89 s | 0.09 s | 66.9 GB |
| Off-primary client IP 20% | 0 / 0 / 100.0M | 4 | 550.73 MB | 44.1 s | 4.41 s | 3.30 TB |
| Off-primary URL hash 20% | 0 / 0 / 100.0M | 4 | 520.25 MB | 41.6 s | 4.16 s | 3.12 TB |
| Off-primary resolution range | 0 / 0 / 100.0M | 4 | 288.14 MB | 23.1 s | 2.31 s | 1.73 TB |
| Four middle-20% ranges | 0 / 78.7M / 21.3M | 601 | 220.66 MB | 17.7 s | 1.77 s | 1.32 TB |
| Four middle-50% ranges | 0 / 49.2M / 50.8M | 601 | 525.68 MB | 42.1 s | 4.21 s | 3.15 TB |

At 25 Mbit/s, the primary middle-20% query has a 3.14-second transfer floor, the four-middle-20% query 70.6 seconds, and the four-middle-50% query 168 seconds.

This proves that primary ordering solves its aligned range query, but does not solve arbitrary exact filtering over a globally cold 100-million-fact map.

## Request-count effect

The primary-aligned query has 200 partial shards: two boundaries for each of 100 marks. Fetching the active predicate, numerator, and denominator as separate plane ranges produces 600 ranges plus one manifest request.

With 32 concurrent HTTP/2 or HTTP/3 streams, 600 ranges require approximately 19 request waves. Ignoring bandwidth:

- At 30 ms round-trip time: about 0.57 seconds of request-wave latency.
- At 50 ms: about 0.95 seconds.
- At 100 ms: about 1.9 seconds.

The pack format should place the primary predicate and all aggregate partials together for each boundary shard. That reduces the aligned query to approximately 200 ranges without materially increasing data bytes.

For the Morton layout, putting each plane in a separate packfile reduced the event-range request count from 11,529 individual plane ranges to 2,271 contiguous runs. Allowing up to 64 KiB of gap overfetch reduced it to 801 requests, but raised data from 288 MB to 333 MB. Range coalescing must therefore be cost-based: trade bandwidth against RTT and connection limits.

## Client memory and hardware

Measured/decompressed working sets for the primary layout:

| Query | Raw GPU upload, 2 partials | Projected upload, 6 partials | Native two-thread read/decode/filter |
|---|---:|---:|---:|
| Primary middle 20% | 39.3 MB | 91.8 MB | 94 ms |
| Primary middle 80% | 39.3 MB | 91.8 MB | 98 ms |
| Four middle 20% | 511.2 MB | 852.0 MB | 1.07 s |
| Four middle 50% | 1.219 GB | 2.032 GB | 2.88 s |
| Off-primary single filter | 1.200 GB | 2.800 GB | 3.10 s measured for client IP |

The six-partial figures are exact raw-memory projections for the same partial fact population. Compressed network sizes cannot be projected exactly without knowing the four additional partial distributions.

The native decode numbers use Apache Arrow's native Zstandard implementation. Browser WASM will normally be slower and must be measured in the production bundle. The benchmark browser did not expose native Zstandard decompression, so a WASM decoder such as [zstd-wasm](https://github.com/bokuweb/zstd-wasm), [parquet-wasm](https://github.com/kylebarron/parquet-wasm), or a custom decoder worker is required. The reference codec is [Facebook/Meta Zstandard](https://github.com/facebook/zstd).

The previous real WebGPU benchmark scanned all 100 million 24-byte atoms in 19 ms for a selective query. For sharded working sets, network and decompression dominate GPU comparison time.

### Recommended client tiers

| Client class | Recommendation |
|---|---|
| Desktop/discrete GPU | 16 GB system RAM minimum; 8 GB VRAM recommended for arbitrary four-predicate/six-partial working sets; 12 GB comfortable. |
| Laptop/integrated GPU | 16 GB shared RAM minimum; cap active decoded/GPU data around 256–512 MB; require spatial/context pruning before numeric filtering. |
| Mobile/low-memory browser | Cap active working set around 32–128 MB. A globally cold 100-million-fact multi-filter query is not viable. Use small context/LOD packs and progressive refinement. |

Test hardware was an Intel i5-12400F with 31.8 GB system RAM and an AMD Radeon RX 6750 XT. AMD documents the RX 6750 XT as a 12 GB RDNA 2 GPU: [official specifications](https://www.amd.com/en/products/graphics/desktops/radeon/6000-series/amd-radeon-rx-6750-xt.html). Browser WebGPU exposed a 128 MB maximum storage-buffer binding, so buffers must remain chunked below that limit.

## Build hardware and storage

The 100-million-fact offline build completed under a two-thread / 5 GB DuckDB limit.

- Public-data projection and Morton-key creation: 608 seconds.
- Morton sort: 31 seconds.
- Morton compression/packing: 30 seconds.
- Primary-field sort: 28 seconds.
- Primary-field compression/packing: 38 seconds.
- One primary layout from source: approximately 11.2 minutes.
- Both experimental layouts: approximately 12.3 minutes after the shared projection.
- Sampled projection peak: 1.06 GiB working set and 1.39 GiB paged memory.
- Temporary disk for both layouts and staging: 5.52 GiB.
- Estimated temporary disk for one primary layout: approximately 3.4 GiB, excluding safety margin and external-sort spill.

The 2-core / 5 GB machine can build 100 million facts, but production builds should use at least 4 CPU cores, 16 GB RAM, and fast NVMe storage to shorten rebuilds and tolerate skew. A one-billion-fact build should budget at least 100 GB of temporary NVMe space; duration and sort spill must be measured rather than assumed linear.

## CDN/origin hardware

The design does not need a query server. It needs object storage plus a CDN that supports byte ranges and high request concurrency.

Storage for 60 maps at 100 million facts each:

| Stored design | Per map | 60 maps |
|---|---:|---:|
| Primary layout only | 1.039 GB | 62.4 GB |
| Morton layout only | 1.144 GB | 68.7 GB |
| Primary + Morton fallback | 2.184 GB | 131.0 GB |
| Four duplicated primary layouts | about 4.16 GB | about 249 GB |

A simultaneous primary-range query from 6,000 cold clients creates approximately 3.6 million range requests with the current three-plane format, or about 1.2 million after bundling primary+partials per boundary shard. The CDN must absorb that burst. A 2-core origin should not directly serve it; the origin should see mostly CDN cache fills.

## One-billion-fact behavior

Primary ordering changes the scaling law for a single aligned range.

For each output mark, only the two boundary shards require fact data. Fully contained interior shards use stored totals, regardless of how many facts they contain.

- If one billion facts still belong to the same 100 large marks, the partial fact population remains near 3.28 million, so aligned-query network usage stays near the measured 10 MB. The manifest and stored pack grow, but the query payload does not grow linearly.
- If one billion facts are spread across 1,000 million-fact marks, boundary work grows roughly tenfold: about 32.8 million partial facts and roughly 100 MB for the aligned query.
- Multi-filter conjunctions remain proportional to the fraction surviving the primary-field pruning. The measured four-middle-20% query would approach roughly 2.2 GB at one billion facts if distributions and compression stayed similar.

The important scale variable is therefore not facts alone. It is also **the number of output marks and facts per mark**. One mark with one million facts is highly shardable. One million marks with a hundred facts each is not; almost every mark becomes one ambiguous shard.

## Recommended production design

### 1. Require a routing field in filter configuration

Each map/view should declare the numeric field most often used for range filtering. Sort by that field inside each context and output mark. This is more valuable than forcing users to choose vague ANY/ALL semantics.

### 2. Shard by context before numeric value

The browser should first select visible geography, time partition, operator, LOD, or another bounded context. Numeric sharding then works on perhaps 1–5% of the global facts, not all 100 million.

### 3. Keep exact tri-state metadata

- OUT: query and shard ranges do not overlap.
- IN: query fully contains the shard range; use stored per-mark totals.
- PARTIAL: fetch and filter atoms.

### 4. Use a cost-based layout planner

The manifest can estimate bytes before fetching. Choose between:

- Primary layout for aligned or multi-filter queries containing the primary field.
- Morton fallback for an off-primary query when it saves enough bytes to justify its extra stored copy.
- Cached resident planes when the client already has them.

### 5. Package for the actual request pattern

- One packfile per secondary predicate plane.
- Primary predicate plus all partials bundled together per shard.
- Byte-range coalescing based on bandwidth and RTT.
- Service Worker + Cache Storage/OPFS/IndexedDB for durable client caching.
- GPU LRU cache for decoded visible contexts.

## Final decision (revised after architecture review)

**Do not proceed with sharding as the general answer to arbitrary exact numeric filtering.** The cold network
and memory results disprove that use.

Retain configuration-driven primary ordering only for products that intentionally restrict the filter
contract, and retain tri-state pruning only after spatial/context tiling has reduced the active population.
An optional Morton copy is not justified by the measured off-primary payloads unless a future real-data
benchmark shows substantially stronger correlation.

Do not claim that generic sharding makes arbitrary cold queries cheap. The benchmark disproves that. The aligned primary query is excellent—9.82 MB over 100 million facts—but broad multi-filter and off-primary queries still require hundreds of megabytes unless a context/spatial layer reduces the working set first.
