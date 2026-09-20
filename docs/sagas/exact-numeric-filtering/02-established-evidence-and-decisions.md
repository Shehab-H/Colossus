# 02 — Established evidence and decisions

**Status:** Active — living evidence baseline for the saga.

**Last verified:** 2026-08-23.

**Estimate:** 0.5 day to reconcile the viewport fact-count discrepancy; otherwise update alongside each R&D
run.

## Motivation

This item separates measured facts from hypotheses. Raw reports remain under [`evidence/`](evidence/); this
file records the conclusions that currently constrain the next design.

## Reference data

The `mobile-coverage` source contains:

- 24,257,354 facts;
- 2,008,081 distinct geometry marks;
- four operators;
- eight quarters;
- 32 possible categorical/temporal cells per geometry mark;
- high-cardinality Float32 fields: `sample_share`, `download_mbps`, `upload_mbps`, and `latency_ms`.

Measured distinct counts from the investigation:

| Field | Distinct values |
|---|---:|
| `operator` | 4 |
| `quarter` | 8 |
| `tests` | 2,463 |
| `sample_share` | 11,415,983 |
| `download_mbps` | 18,411,577 |
| `upload_mbps` | 15,417,872 |
| `latency_ms` | 4,180,160 |

Conclusion: categorical and temporal domains are appropriate slab axes; the nearly unique numeric values are
not. Multiplying numeric domains creates a theoretical cell space near `10^33` without adding useful product
semantics.

## Existing client slab and remote fold

The archived companion benchmark established:

- fixed Europe viewport: 12 leaf tiles and 583,511 rendered marks;
- client pre-reduced slab fold: 51.96 ms p50 / 136.35 ms p95 across the complete viewport in the corrected
  route comparison;
- worst individual coverage tile after plane splitting: approximately 2.56 ms p50 / 36.30 ms p95;
- cold active-measure companion transfer for the viewport: roughly 7–8 MB depending bake/report version;
- remote DuckDB fold: approximately 4.07 seconds p50 / 4.87 seconds p95 compute;
- one UInt16 result over the viewport: approximately 1.17 MB.

The four-second remote result is not a lower bound for near-data filtering. The request SQL currently derives
tile and grid indices, chooses real versus merged mark keys, performs `DISTINCT`, assigns `mki` with a window,
recreates grain cells, and only then folds. Persisting those stable identities is the primary correction for
the next proof.

### Population discrepancy

The adaptive-index prototype read the current baked facts Parquet over the recorded 12-tile rectangle and
found **7,250,351 facts / 583,511 marks**, maximum 32 facts per mark. The archived remote-fold report says it
rebuilds cells from **2.4 million viewport facts**. Both cannot be cited as the same population without an
explanation.

The next run must emit and compare:

- exact tile membership counts from the manifest/render tiles;
- exact facts-Parquet row count before context;
- row count after any operator/quarter context;
- distinct geometry marks;
- per-tile `mki` counts;
- bake version and file checksum.

No new headline benchmark is accepted until this discrepancy is resolved.

## CPU scan baselines

The original 24.26-million-fact ClickHouse query measured roughly:

| Operation | Result |
|---|---:|
| Four-predicate scan | ~260 ms |
| Predicate plus exact per-mark color | ~660–730 ms |
| Complete grouped CPU time | ~1,416 ms |
| Query-local memory | ~144 MiB |

Two, four, and eight simultaneous scans degraded to approximately 1.4, 4, and 11 seconds per query. Warm page
cache did not remove decoded-column and CPU work.

A disposable resident AVX2 layout over the same source found:

| Operation | Result |
|---|---:|
| Four exact predicates | 15–17 ms |
| Predicate plus one weighted color | 75–80 ms |
| Predicate plus five values | ~175–210 ms |
| Resident representation | ~652 MiB observed |

Conclusion: compact resident scans can identify facts quickly at 24M scale, but exact multi-measure reduction
and distinct-query concurrency remain expensive. Full scans are useful baselines, not the universal answer.

## GPU evidence

The 100-million-atom WebGPU benchmark measured:

| Query | Two-core CPU | WebGPU |
|---|---:|---:|
| All rows | 2,997 ms | 43.3 ms |
| One middle-20% range | 2,769 ms | 19.0 ms |
| Four middle ranges | 2,885 ms | 18.5 ms |
| Reject all | 2,747 ms | 15.0 ms |

Fact membership and mark assignment hashes matched. A logical one-billion-atom repetition completed in
186.2 ms only because the same 100M resident population was scanned ten times; it did not prove one billion
distinct atoms fit in GPU memory.

The raw 100M representation was 2.4 GB, compressed Parquet approximately 880.6 MB, native two-thread decode
2.424 seconds, and approximate GPU allocation 2.533 GB.

Decision: the GPU comparison kernel is fast, but moving a global exact fact pack to every client is rejected.

## Client sharding evidence

The exact 100-million-fact sharding benchmark compared a four-dimensional Morton layout and a per-mark layout
ordered by one primary numeric field.

| Cold primary-layout query | Network | Raw client working set, two partials |
|---|---:|---:|
| Primary middle 20% | 9.82 MB | 39.3 MB |
| Off-primary middle 20% | 550.73 MB | 1.20 GB |
| Four middle 20% | 220.66 MB | 511.2 MB |
| Four middle 50% | 525.68 MB | 1.219 GB |

Every hybrid result matched the full scan. The aligned primary range was excellent because each mark had at
most two boundary shards. Off-primary and multidimensional queries made nearly all shards ambiguous.

Decision: retain primary ordering only as a restricted optimization after spatial/context pruning. Reject it
as the general arbitrary-filter architecture.

## Adaptive mark-index evidence

The exact prototype used one four-dimensional min/max header per mark, direct scans for marks with at most 64
facts, and a median-split 4D BVH with 64-fact leaves above that threshold. The output clear/write cost for a
dense visibility bitmap was included.

### Current real viewport

| Two pinned logical processors | p50 | p95 | p99 |
|---|---:|---:|---:|
| `ANY`, 1D middle 20% | 8.71 ms | 11.07 ms | 14.67 ms |
| `ANY`, 4D broad 80% | 11.62 ms | 13.40 ms | 14.55 ms |
| `ANY`, 4D middle 50% | 17.42 ms | 20.51 ms | 23.16 ms |
| `ANY`, 4D narrow 10% | 9.63 ms | 14.26 ms | 17.92 ms |
| `ANY`, 4D needle 1% | 5.87 ms | 9.87 ms | 11.65 ms |
| `ALL`, 4D middle 50% | 2.73 ms | 4.03 ms | 5.29 ms |

No real mark exceeded 32 facts, so the adaptive policy built zero trees. The winning real-data representation
was simply a mark header plus contiguous direct facts.

### Uniform heavy marks

| Dataset / query | Flat p99 | BVH p99 |
|---|---:|---:|
| 24M facts, 5,000 heavy marks, broad 80% | 21.37 ms | 25.38 ms |
| same, middle 50% | 31.11 ms | 21.59 ms |
| same, narrow 10% | 63.41 ms | 22.80 ms |
| same, needle 1% | 67.41 ms | 15.28 ms |
| 100M facts, 5,000 heavy marks, middle 50% | 55.07 ms | 25.50 ms |
| same, narrow 10% | 180.85 ms | 18.60 ms |
| same, needle 1% | 162.21 ms | 11.65 ms |

Separate runs placed approximately 902,753 and 4.07 million facts into each of 24 heavy marks; selective BVH
p99 remained below roughly 11 ms, while middle-50% p99 remained about 21–23 ms.

Every visibility bitmap matched the exact flat reference.

### Memory correction

For the 100M / 5,000-heavy-mark run:

| Component | Approximate modeled memory |
|---|---:|
| Four raw Float32 predicate columns | 1.53 GB |
| BVH nodes | 234 MB |
| Mark bounding boxes | 18 MB |
| Offsets and roots | 5 MB |
| Complete representation | 1.78 GB |
| Observed working set | 2.03 GB |

The 1.78 GB is shared by every query in one process. It is not per user. A per-user visibility bitmap over
583,511 marks is approximately 73 KB. The benchmark was fully warm and did not prove cold mapped-file latency.

## Concurrency evidence

One hundred unrelated middle-50% 4D queries submitted against the real viewport on two pinned logical
processors produced:

| Metric | Result |
|---|---:|
| response p50 | 1,011 ms |
| response p95 | 1,640 ms |
| response p99 | 1,646 ms |
| burst wall time | 1,650 ms |
| throughput | 60.6 queries/s |
| oversubscribed service p50 / p95 | 314 / 474 ms |

Decision: a good single-query index does not meet the stated concurrency with isolated traversals. The next
concurrency proof must compare active-query-mask shared traversal and horizontal capacity.

## Database and index conclusions

- A separate ordered range index for each numeric dimension is useful because the `AND` predicate allows the
  most selective dimension to drive fact candidates.
- If two or more ranges are selective, exact `fact_id` posting bitmaps can be intersected before fetching
  partials.
- Broad ranges make ordered indexes enumerate much of the table. They need a scan, complement plan, or
  aggregate multidimensional tree.
- DuckDB provides automatic zonemaps and explicit ART indexes. Current documentation positions ART for point
  and very selective single-column predicates; ordinary index scans do not use multi-column indexes.
- DuckDB does not document automatic four-ART bitmap intersection. This must be observed with
  `EXPLAIN ANALYZE`, not assumed.
- DuckDB ART buffers load lazily from disk but are currently not evicted by the buffer manager. Four large
  indexes can threaten the 5 GB process limit after all are exercised.
- DuckDB explicitly optimizes larger analytical queries rather than many tiny concurrent queries. Its current
  fold and prior scans remain useful baselines, but DuckDB is no longer the next indexing experiment.

Official references:

- [DuckDB indexing guide](https://duckdb.org/docs/current/guides/performance/indexing)
- [DuckDB indexes](https://duckdb.org/docs/current/sql/indexes)
- [DuckDB workload tuning](https://duckdb.org/docs/current/guides/performance/how_to_tune_workloads)
- [DuckDB query-plan inspection](https://duckdb.org/docs/current/guides/meta/explain)

## Lance + DataFusion experiment result

The measured experiment used Lance 10.0.0 and DataFusion 54.1.0 on the complete 24,257,354-fact real map.
The full Lance conversion produced 299.35 MiB in 2.104 seconds with 207.44 MiB peak working set. Global
operator/quarter bitmap indexes plus a timestamp B-tree added 231.34 MiB.

The global index layout failed. A one-cell broad range identified only 784,239 result rows, but generated
783,537 physical ranges, read 162.95 MiB, took 669.03 ms warm, and helped push process peak residency to
1.81 GiB. An unindexed monolithic scan took 385.90 ms. Fewer logical rows did not mean less physical work.

Physical category routing succeeded. One real operator/quarter shard contained 784,255 facts. Without an
index its broad fold took 56.91 ms and read 12.15 MiB. After sorting by timestamp, its B-tree returned a 50%
range as four contiguous physical ranges; the exact fold took 34.21 ms and read 5.85 MiB. DuckDB and
Lance/DataFusion returned identical row counts and aggregate checksums.

This establishes the component boundary. Lance remains the immutable category/time-local storage and
selective boundary-read layer. DataFusion remains the embedded Arrow execution surface. Broad billion-fact
ranges require the smallest possible custom DataFusion provider over category-sharded canonical time
aggregates—not a custom query engine and not more global scalar indexes. Full measurements and limitations
are in [item 05](05-production-shaped-lance-datafusion-proof.md).

Official references:

- [Lance index model](https://lance.org/format/index/)
- [Lance and DataFusion integration](https://lance.org/integrations/datafusion/)
- [DataFusion embedded and custom-provider surface](https://datafusion.apache.org/user-guide/introduction.html)

## Output and network lower bounds

For 583,511 marks:

| Result | Bytes | Ideal floor at 100 Mbit/s |
|---|---:|---:|
| visibility bitmap | ~72,939 | ~5.8 ms |
| UInt8 style code | 583,511 | ~46.7 ms |
| UInt16 code | 1,167,022 | ~93.4 ms |
| Float32 value | 2,334,044 | ~186.7 ms |

One hundred simultaneous Float32 responses in 100 ms imply roughly 18.7 Gbit/s of egress before framing.
No index removes this result-size bound.

## Rejected or bounded approaches

| Approach | Disposition | Reason |
|---|---|---|
| Numeric values as slab/cube axes | Rejected | Cartesian/high-cardinality explosion. |
| Generic per-request Parquet fold | Rejected in current form | Reconstructs stable tiling/mark/grain state; seconds per request. |
| Global fact planes in browser | Rejected | Cold network, decode, and client memory dominate. |
| Generic client sharding | Rejected as general answer | Unaligned exact queries still transfer hundreds of MB. |
| Primary-field client ordering | Bounded optimization | Excellent only for declared aligned ranges after context pruning. |
| Full columnar scan | Baseline/fallback | Fast for small warm viewports; bandwidth-linear and concurrency hungry. |
| Separate ordered indexes | Leading selective path | Exact but broad postings and random partial retrieval can dominate. |
| Adaptive aggregate BVH | Leading broad/heavy path | Warm identification passed; disk, aggregates, LOD, and concurrency unproven. |
| Pinot range/Star-Tree | Reference benchmark | Useful algorithms; operational footprint and configured dimensions conflict with 5 GB/general ranges. |
| HeavyDB GPU server | Deferred hardware class | NVIDIA/hot-data requirements do not match current server budget. |
| HashedCubes/Nanocubes/Falcon | Inspiration only | Their speed depends on bins, hierarchy, resolution, or restricted aggregates. |

## Integration surface

- Raw long-form evidence: [`scaling-investigation.md`](evidence/scaling-investigation.md).
- GPU evidence: [`gpu-resident-benchmark.md`](evidence/gpu-resident-benchmark.md).
- Client sharding evidence: [`client-sharding-benchmark.md`](evidence/client-sharding-benchmark.md).
- Archived client/remote comparison:
  [`R4-BUILD.md`](../../archive/2026/2026-08-10-pre-index/companion-scale/R4-BUILD.md).
- Current remote executor:
  [`DuckDbFoldExecutor.cs`](../../../src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs).

## Risks

- Several benchmark populations are synthetic and specifically stress one distribution.
- Warm page residency hid storage behavior in the adaptive-index result.
- Identification benchmarks did not store node aggregate partials.
- The adaptive benchmark used one fixed leaf-level mark mapping, not complete LOD translation.
- The 60-query 100M stress percentiles have less statistical strength than the 160-query real/24M runs.
- Two pinned logical processors on the i5-12400F may not exactly reproduce two cloud vCPUs.

## Acceptance evidence

This item is accepted as the current baseline when:

- every headline number links to raw evidence or a reproducible command;
- the 7.25M/2.4M viewport discrepancy is resolved;
- the next run records build/version/checksum and retains its query corpus;
- no conclusion labels an unmeasured cold, aggregate, LOD, or concurrent property as proven.

## Next action

Proceed to layout D in [item 05](05-production-shaped-lance-datafusion-proof.md): expose category-sharded
canonical timestamp aggregates through a minimal DataFusion provider, persist real `(tile_key, mki)`/LOD
identities, and prove complete-context parity. Do not repeat the rejected global scalar-index layout.
