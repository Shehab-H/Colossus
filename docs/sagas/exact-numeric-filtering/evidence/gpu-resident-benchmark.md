# Numeric filter scaling benchmark

> **R&D disposition — 2026-08-21:** This benchmark proves that resident GPU filtering is
> fast. It does not justify transferring a global fact pack to each browser. The subsequent
> sharding benchmark found unacceptable cold transfer and memory costs for arbitrary filters, so
> the client-side sharded GPU architecture is no longer the general recommendation.

## Verdict

The benchmark confirms both sides of the argument:

1. Dynamic server scanning is not viable on a 2-core / 5 GB server at the proposed concurrency.
2. A frontend GPU can filter and reduce 100 million exact filter atoms interactively once the data is resident.
3. Moving every fact for every map to the GPU at once is not viable. Network transfer and GPU memory, not filter compute, become the limiting resources.

The practical design is therefore a **client-side, sharded GPU engine**: static compressed shards are fetched from object storage/CDN, the browser classifies shards using small exact metadata, and WebGPU scans only ambiguous resident shards. No per-request server query is required.

## What was tested

- Source: the official [ClickHouse ClickBench](https://github.com/ClickHouse/ClickBench) production-derived dataset.
- Physical population: 99,997,497 events.
- Four real/real-derived numeric predicates: second-of-week, low 24 bits of client IP, low 24 bits of URL hash, and resolution width.
- Two aggregate partials: weighted response-time numerator and weight denominator.
- Atom representation: six `Float32` values, or 24 bytes per fact.
- Mark layout: 12 consecutive facts per synthetic mark, producing 8,333,141 marks.
- Raw input: 2,399,939,928 bytes.
- Per-mark GPU output: 133,330,256 bytes.
- CPU limit: DuckDB preparation limited to two threads and 5 GB; query baseline processed one 96 MB chunk at a time and was limited to at most two numerical-library threads.
- GPU: AMD Radeon RX 6750 XT through real browser WebGPU (`amd`, `rdna-2`).
- Browser: Chromium 151.

The numeric values are real, but the grouping of every 12 rows into a mark is synthetic. This tests the exact filter/reduction engine and a high-output-count layout; it does not reproduce a particular map's geographic distribution.

## Query results

| Query over 99,997,497 atoms | Selected | 2-core-capped CPU median | WebGPU median | Speedup |
|---|---:|---:|---:|---:|
| All rows | 99,997,497 | 2,997.2 ms | 43.3 ms | 69× |
| One middle-20% range | 20,009,976 | 2,769.1 ms | 19.0 ms | 146× |
| Four middle ranges | 7,214,723 | 2,884.9 ms | 18.5 ms | 156× |
| Reject every row | 0 | 2,746.5 ms | 15.0 ms | 183× |

Selectivity barely changes the CPU scan because it still reads all atoms. The GPU also reads the atoms, but can do the comparisons and per-mark reduction at roughly 126–160 GB/s of input bandwidth for the selective cases.

The logical one-billion-atom stress test repeated the resident 100-million population ten times. It scanned 23,999,399,280 logical input bytes in a median **186.2 ms**, or about **128.9 GB/s**. This is a compute/bandwidth result only: it does not claim that one billion distinct atoms fit in GPU memory.

Browser WebGPU did not expose timestamp queries, so timings use submission-to-completion wall time via `queue.onSubmittedWorkDone()`. Resolution was 0.1 ms.

## Exactness check

For the middle-20% query:

- CPU selected count: 20,009,976.
- GPU selected count: 20,009,976.
- CPU and GPU per-mark selected-count hash: 1,932,504,939 on both sides.
- Weight denominator total matched exactly: 30,407,701,836.
- Weighted numerator differed by 68 over approximately 3.324 trillion, about 0.000000002%, because floating-point additions can be associated differently.

This proves exact filter membership and exact mark assignment for the tested representation. If aggregate bits must also be identical across CPU and GPU, the format needs a declared arithmetic contract: fixed-point/integer partials, or an explicitly identical reduction order.

## Disk, network, and memory

| Item | Measured value |
|---|---:|
| Raw 4-predicate + 2-partial atoms | 2.400 GB |
| Parquet + Zstandard level 3 | 880.6 MB |
| Compression ratio | 2.73× |
| Native Arrow decode, two threads | 2.424 s |
| Raw localhost fetch + GPU upload | 7.913 s |
| Approximate resident GPU allocation | 2.533 GB |

Compressed bytes by plane were:

| Plane | Compressed size |
|---|---:|
| Second-of-week | 343.0 MB |
| Client-IP low 24 bits | 157.4 MB |
| URL-hash low 24 bits | 255.7 MB |
| Resolution width | 29.4 MB |
| Weighted numerator | 65.4 MB |
| Weight denominator | 29.4 MB |

A query using only the first predicate plus the two aggregate partials would need about 437.8 MB compressed instead of the full 880.6 MB. This is why predicate and partial planes should be independently fetchable.

The 2.424-second decode is native Arrow, not browser WASM, so it is an optimistic upper bound for frontend decompression. [parquet-wasm](https://github.com/kylebarron/parquet-wasm) provides streaming Parquet-to-Arrow decoding and is a reasonable prototype dependency, but it must be benchmarked in the actual browser. DuckDB-Wasm can also read Parquet in-browser, but its official documentation notes a 4 GB WebAssembly memory limit and single-threaded default, so it should not own a monolithic working set of this size: [DuckDB-Wasm limitations](https://duckdb.org/docs/stable/clients/wasm/overview).

## General storage formula

For `N` filter atoms, `D` `Float32` predicate fields, and `P` `Float32` aggregate partials:

`raw bytes = N × 4 × (D + P)`, before IDs, validity flags, offsets, or compression.

Examples:

| Scale | 4 predicates + 2 partials | 4 predicates + 6 partials |
|---|---:|---:|
| 100 million atoms | 2.4 GB | 4.0 GB |
| 1 billion atoms | 24 GB | 40 GB |
| 60 maps × 100 million atoms | 144 GB | 240 GB |

At the measured bandwidth, 4 predicates + 6 partials would have a rough selective-query kernel floor near 32 ms for 100 million atoms and 310 ms for one billion atoms. Those are linear bandwidth projections, not measurements. Capacity is the harder problem: 40 GB cannot be resident on this GPU.

## Why the 2-core server fails

The middle-range CPU query consumed about 2.73 CPU-seconds and completed in 2.77 wall-seconds. Two cores can therefore sustain at most roughly **0.73 queries/second**, even before serialization, network output, map rendering, or other services.

If 5,000–6,000 users issue one query together, the theoretical drain time is about **114–137 minutes**. That validates the concern about 50–60 maps with 100 concurrent users each.

Frontend GPU compute distributes that work across users' machines, but first-load egress remains shared infrastructure. Sending the measured 880.6 MB compressed pack to 6,000 cold clients is about 5.28 TB. At 100 Mbit/s, one client needs at least 70 seconds for the full pack before decode; at 1 Gbit/s, at least 7 seconds.

## The recommended exact architecture

1. Build immutable filter atoms offline. Each atom belongs to exactly one output mark and contains its complete predicate tuple plus distributive aggregate partials.
2. Store atoms in small geographic/context shards, with predicate and partial planes separated and compressed.
3. Give each shard a tiny manifest containing row count, output-mark mapping, per-predicate minimum/maximum, and full-shard aggregate totals.
4. Let the browser classify each shard for a range query:
   - **OUT:** a predicate interval cannot overlap the shard; fetch/scan nothing.
   - **IN:** every predicate interval fully contains the shard; use the stored totals.
   - **PARTIAL:** the shard might contain both passing and failing atoms; scan its atom planes in WebGPU.
5. Keep only visible or soon-visible shards in an LRU GPU cache. Fetch static bytes through object storage/CDN, not a query server.
6. Reduce selected partials per mark in WebGPU. Very large marks use temporary workgroup partial reductions; that is an execution strategy, not a permanent search tree or server route.

This approach is exact. OUT and IN decisions follow directly from interval containment. PARTIAL shards are evaluated atom-by-atom, so ambiguous metadata never produces an approximation. Aggregate correctness follows because count, sum, and weighted numerator/denominator are distributive over the selected atom partition.

Worst case, every shard is PARTIAL and the system falls back to the measured flat GPU scan. Best case, most shards are accepted or rejected from a few bytes of metadata and neither their predicate planes nor partial planes need to cross the network.

## Decision

**Go** for a frontend GPU prototype with sharded, columnar filter atoms. The 100-million-atom compute result is strong enough.

**Do not go** with a design that loads an entire billion-atom dataset, or dozens of 100-million-atom maps, into each browser. The math rejects that design even though the kernel is fast.

The next benchmark should use real map/context shards, a browser WASM decoder, visible-tile loading, and three hardware classes: the current desktop GPU, an integrated laptop GPU, and a mobile/low-memory device. The success metric should include click-to-result time after a cold and warm load, not only kernel time.
