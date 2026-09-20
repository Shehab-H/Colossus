# 06 — Disk, memory, and concurrency proof

**Status:** Candidate — partially measured for Lance layouts A–C; repeat the full matrix for aggregate layout D.

**Last verified:** 2026-08-23.

**Estimate:** 2–4 engineering days after item 05, including cold-cache setup, concurrent runs, evidence
capture, and deletion of large generated artifacts.

## Motivation

The warm single-query prototype proves that exact filtering can be computationally fast. It does not prove
that a production server can page the representation from disk, keep many maps available, or serve many
unrelated users on two cores.

This item separates four resources that are easy to blur together:

- permanent disk bytes;
- resident process memory;
- operating-system page cache/file-backed residency;
- per-query CPU, scratch memory, and output buffers.

It then measures the queueing behavior caused by one, ten, and one hundred distinct filters.

## Correct interpretation of the 100-million-fact prototype

The reported 1.78 GB was **not per user** and was **not only an index**. It was approximately:

| Component | Approximate bytes | Meaning |
|---|---:|---|
| Four raw predicate arrays | 1.53 GB | Four Float32 values for 100 million active facts. |
| Mark headers, roots, offsets, BVH nodes | 255 MB | Shared structural metadata and aggregate tree. |
| Complete serving representation | 1.78 GB | Shared once per loaded map/process in the prototype. |
| Observed process working set | 2.03 GB | Representation plus runtime/allocator overhead. |
| One 583,511-mark visibility bitmap | 73 KB | Typical per-result/user state before protocol overhead. |

Those numbers came from a deliberately warmed in-memory representation. A production design can store the
same logical structures in immutable files and fault pages on demand, but file-backed storage does not make
physical reads free. It changes when and how memory is populated.

Raw evidence: [GPU-resident benchmark](evidence/gpu-resident-benchmark.md) and
[scaling investigation](evidence/scaling-investigation.md).

## Resource model

### Disk

Disk holds immutable map versions, fact columns, mark directories, numeric indexes, and aggregate nodes. Disk
capacity can be much larger than RAM, but a query's latency includes every required page that is not already
resident.

### Process RSS

The hard serving limit is 5 GB observed resident memory for the process, not merely an engine's configured
buffer or cache limit. Runtime allocations, index pages, native libraries, query results, and memory outside
the engine's accounting all count.

### OS page cache

Memory-mapped or normal file reads can share clean immutable pages across requests and sometimes across
processes. Those bytes still consume machine RAM even if accounting does not look like a private managed
array. A 60-map deployment cannot assume every complete index remains resident.

### Per-query state

Ranges, traversal stacks, candidate fact IDs, group tables, visibility bitmaps, value columns, and
serialization buffers scale with concurrent in-flight queries. Immutable indexes are shared; mutable query
state is not.

## Why cold full scans cannot meet the target

As an illustrative lower bound, reading 2 GB from an ideal sustained 3 GB/s device takes about 667 ms before
predicate comparisons, aggregation, or output. Random access and ordinary cloud disks are often worse. Thus
sub-50-ms cold performance cannot depend on reading an entire large representation.

The architecture needs one or more of:

- small resident headers/directories that reject work;
- spatial partitions so only the active viewport is touched;
- selective ordered/index pages;
- aggregate nodes that accept whole subtrees;
- deliberate prefetch of likely active maps/tiles;
- enough fleet RAM/I/O capacity for the active working set.

## Test environment

Record and hold constant:

- CPU model, exactly two available cores/logical processors, and affinity/cgroup configuration;
- total RAM, process 5 GB limit, and any container/cgroup memory ceiling;
- operating system, kernel, filesystem, and mount options;
- storage device/model, local versus network attachment, measured sequential/random bandwidth and latency;
- Lance, DataFusion, Arrow, and native runtime versions plus all relevant settings;
- database and index bytes, map partitions, and compression;
- server process count and whether pages are shared between processes;
- background load, antivirus/indexer exclusions if appropriate, and power mode.

The most representative production run should be Linux/container based if production will be Linux. Windows
results remain valuable for local development, but its page-cache controls and allocator behavior must not be
presented as the final deployment proof.

## Cache-state definitions

Every result must use one of these explicit labels:

| State | Required setup | What it answers |
|---|---|---|
| Hot process | Same process has executed the query family and touched its indexes/data. | Best steady-state interaction. |
| Prefetched data | Required database/data pages are in OS cache, but a fresh process/connection has not loaded private index state. | Restart and shared-page behavior. |
| Cold process, warm OS | Process restarted while OS page cache remains populated. | Distinguishes private allocations from file cache. |
| Genuinely cold | Relevant data/index pages evicted from OS cache or read from a fresh controlled block device/cache namespace. | First activation and inactive-map behavior. |

Closing and reopening the serving process is **not** a genuine cold test by itself. Rebooting is also too
blunt unless the storage and background warmers are controlled.

On Linux, use a dedicated test machine/container and a safe, documented method such as per-file cache advice,
a disposable loop/block device, or a benchmark-sized cache namespace. Never use broad cache-dropping commands
on a shared machine. On Windows, use a dedicated benchmark machine and corroborate cold state through process
I/O counters and a dataset larger than available cache; if true eviction cannot be guaranteed, label the run
“cold process, OS state unknown.”

## Metrics

Capture per phase and end-to-end:

- wall p50, p95, p99, maximum, and timeout/cancellation count;
- CPU time and effective core utilization;
- throughput and queue wait separately from execution time;
- process RSS, private bytes, mapped/file-backed bytes where available;
- major/minor page faults or platform equivalent;
- process read operations and bytes, storage IOPS/throughput/latency;
- DataFusion memory-pool usage, Lance index/cache residency where observable, and spill bytes;
- candidate facts, visited index/BVH nodes, output live marks, and output bytes;
- allocation volume and peak per-query scratch;
- database/index sizes and cache-state preparation duration.

An end-to-end response can miss 100 ms even when execution is below 50 ms because it waited in a two-core
queue. Always report both.

## Experiment matrix

### Population

1. Complete real map: 24.26 million facts.
2. Production-shaped 100-million-active-fact corpus only after the real map passes or exposes a precise
   bottleneck worth scaling.
3. One-billion-fact projection from measured bytes/page visits plus a targeted partition-scale validation;
   do not generate one billion rows merely to fill disk.

### Cache state

Run the four cache states above for each viable plan from item 05.

### Concurrency

Run:

- one filter in flight;
- ten unrelated filters released together and as a paced arrival stream;
- one hundred unrelated filters released together and as a paced arrival stream.

Use unique seeded ranges with low expected overlap. Include cancellation from slider supersession. Do not count
result-cache hits toward the core capacity result.

### Query shape

Include selective-driver, broad-conjunction, high-hit, empty, and slider-delta families. Include leaf and
internal LOD output because grouping/output size changes with zoom.

### Map residency

Simulate at least:

- one hot map;
- several active maps whose resident headers/directories fit memory;
- a 50–60-map catalog with only a controlled active working set;
- repeated switches between maps that exceed RAM, to expose page-fault thrashing.

Do not duplicate the full map representation once per user. If several server processes are required, verify
which clean file pages are physically shared and which DuckDB/index structures are privately replicated.

## Baseline concurrency evidence

The previous real-viewport burst of 100 unrelated filters reached approximately 60.6 queries/second but
response p95 rose to 1.64 seconds on two pinned logical processors. That result is consistent with queueing:
even a fast CPU-bound query cannot give 100 simultaneous users sub-100-ms latency when each one independently
scans/traverses on only two cores.

Raw evidence: [scaling investigation](evidence/scaling-investigation.md).

The measured category/time-sorted Lance shard adds a newer baseline. With twelve logical DataFusion workers,
64 simultaneous broad queries reached p95 3.734 seconds and 1.967 GiB peak working set. Limiting execution to
six active queries improved throughput from 16.80 to 29.73 queries/second, reduced p95 to 2.049 seconds, and
reduced peak working set to 308.59 MiB. Eight users with the same cap reached p95 369.48 ms and 280.02 MiB.
The 5 GB limit is therefore not the current failure; CPU queueing is. Full plans, I/O, and E2E measurements
are in [item 05](05-production-shaped-lance-datafusion-proof.md).

## Shared-query traversal experiment

High-cardinality queries rarely have identical result-cache keys, but they can still share **index reads and
tree traversal**.

For a batch of up to 64 or 128 active filters:

1. represent active queries as a bit mask;
2. visit a BVH/node once;
3. compare its four-dimensional bounds against all active query boxes;
4. split the mask into disjoint, fully-contained, and partial queries;
5. add stored aggregate partials for contained queries;
6. descend only for the partial-query mask.

This does not make queries semantically overlap. It amortizes instruction fetch, node/page reads, and cache
misses across unrelated boxes. It is the most promising concurrency-specific experiment if ordinary
Lance + DataFusion queries pass single-user latency but fail the two-core burst gate.

Measure batching delay explicitly. A 2–5 ms coalescing window may be acceptable inside a 100 ms target; a
large window can improve throughput while hurting interaction latency.

## Scheduling model if batching is insufficient

Two cores impose a service-rate limit. The serving design must include:

- bounded work queue and per-map/global concurrency limits;
- cancellation of superseded slider revisions before expensive aggregation/serialization;
- admission control or backpressure rather than unbounded memory growth;
- optional priority for visible interaction over prefetch/background work;
- an explicit scale-out model: requests/core, required replicas for target arrival rate, RAM per active map,
  and shared storage bandwidth.

The requirement “50–60 maps with 100 concurrent users each” cannot mean 5,000–6,000 simultaneous CPU-heavy
queries on one two-core server. If that is the arrival model, horizontal capacity is a mathematical
requirement even with excellent indexing.

## Network and response proof

Measure compressed and uncompressed response bytes separately:

- bitmap-only visibility;
- bitmap plus one compact palette/category column;
- bitmap plus one Float32 style value;
- inspection payload requested on demand.

For the current 583,511-mark viewport, bitmap-only is about 73 KB while one Float32 column is about 2.33 MB.
At 100 Mbit/s, 2.33 MB takes roughly 186 ms of ideal payload transmission before latency and protocol
overhead. The API therefore needs an explicit output budget, not only a database latency budget.

## Memory acceptance gates

- Peak process RSS stays below 5 GB after exercising every numeric index, query family, and active partition.
- No monotonic RSS growth across a long mixed-query run.
- Per-query scratch has a measured upper bound and cancellation releases it.
- A 60-map catalog does not eagerly deserialize every index.
- Multiple workers do not privately duplicate enough index memory to violate host capacity.
- Temporary spill is bounded and does not consume the user's remaining disk.

## Latency/capacity acceptance gates

- Hot single-query complete server path p99 at or below 50 ms.
- Ordinary prefetched click-to-visible p95 at or below 100 ms under a declared network/output contract.
- Genuine-cold latency reported honestly; if it exceeds 100 ms, the product has an explicit activation/prefetch
  policy rather than an impossible promise.
- Ten- and hundred-query runs report queue and execution distributions; the chosen batching/scheduling/replica
  model meets the declared arrival-rate SLO.
- Performance does not depend on identical query cache hits.

## Risks

- An OS-warm benchmark mislabeled as disk-backed/cold creates false confidence.
- ART pages may accumulate outside normal eviction and make later queries fail the RSS limit.
- Random fact gathers can turn a selective logical plan into an IOPS bottleneck.
- Separate worker processes may share file data pages but duplicate index/allocator state.
- Page-fault storms across dozens of maps can destroy tail latency even when one-map p99 is excellent.
- Response value columns can dominate both memory and network after predicate work is optimized.
- Shared traversal improves throughput only when enough queries are concurrently available; batching delay must
  remain bounded.

## Integration surface

- Existing concurrency evidence: [scaling investigation](evidence/scaling-investigation.md).
- Candidate shared tree: [item 03](03-leading-hybrid-architecture.md).
- Production-shaped dataset and query corpus:
  [item 05](05-production-shaped-lance-datafusion-proof.md).

## Acceptance evidence

A report under `evidence/` containing the exact artifact versions, hardware, cache preparation, process
settings, full latency/queue distributions, RSS and I/O time series, database/index bytes, correctness
checksums, concurrency arrival model, network payloads, failures, and a capacity projection for 60 maps.

## Cleanup

Before deletion, verify that the compact report, query seed/corpus, plans, checksums, and build manifest are
present and readable. Then remove only the explicitly named generated databases, copies, synthetic datasets,
and profiler traces. Retain the real source bake and all Markdown evidence.

## Next action

Repeat this matrix for the category-sharded aggregate provider selected by
[item 05](05-production-shaped-lance-datafusion-proof.md), including cancellation, multi-map residency, and a
controlled Linux cold-cache run. Direct independent queries already fail the concurrency target while one
query is acceptable; compare bounded admission with shared active-query-mask aggregate traversal before
changing predicate semantics.
