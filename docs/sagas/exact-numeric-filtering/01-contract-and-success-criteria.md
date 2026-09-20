# 01 — Contract and success criteria

**Status:** Active — semantic owner confirmation is required before implementation.

**Last verified:** 2026-08-23.

**Estimate:** 0.5–1 day to resolve the open semantic choices and pin fixtures; no production code is included
in this estimate.

## Motivation

An index can be extremely fast and still answer the wrong question. This investigation repeatedly crossed
three levels that must remain distinct:

1. a fact satisfies the conjunction of numeric predicates;
2. a rendered mark survives according to facts currently belonging to that mark;
3. the selected facts are reduced into exact displayed and inspected measures for that mark at the current
   LOD.

This item defines the authority against which every benchmark must be compared.

## Fact predicate

Let `B(m,z,t)` be the facts represented by rendered mark `m` in tile `t` at zoom `z` after the existing
categorical, temporal, spatial, and authored bake context has been applied.

Let the numeric filter be four inclusive ranges:

```text
P(f) =
    low₀ ≤ f.numeric₀ ≤ high₀
AND low₁ ≤ f.numeric₁ ≤ high₁
AND low₂ ≤ f.numeric₂ ≤ high₂
AND low₃ ≤ f.numeric₃ ≤ high₃
```

All four comparisons apply to the same fact. This is why separate numeric-index results must be intersected
by `fact_id`, never merely by mark ID.

Counterexample:

| Fact in mark M | Download | Upload |
|---|---:|---:|
| A | 50 — passes | 100 — fails |
| B | 10 — fails | 15 — passes |

For `download 40–60 AND upload 10–20`, the mark appears in both per-column mark sets but no fact satisfies
the conjunction. A mark-level bitmap intersection would be a false positive.

## Mark survival modes

The current per-fact fold behaves as `ANY`:

```text
survive_ANY(m) = there exists f in B(m,z,t) where P(f)
```

The proposed optional `ALL` configuration is:

```text
survive_ALL(m) = B(m,z,t) is not empty and every f in B(m,z,t) satisfies P(f)
```

`ALL` is cheap because it can be answered by the mark's four-dimensional bounding box. `ANY` requires exact
facts or a hierarchical index when the root box only partially overlaps the query.

### Owner decision still required

The product must confirm whether `ANY`/`ALL` controls only mark visibility or also changes the aggregate
population:

- **Recommended:** numeric predicates always select `I = {f in B | P(f)}` and measures reduce `I`; `ANY` is
  the normal non-empty rule. `ALL` is a visibility policy that either keeps the complete `B` aggregate or
  hides the mark. This makes `ALL` useful and explainable.
- **Alternative:** `ALL` still reduces `I`. Because `ALL` implies every fact passed, `I = B`, so the result is
  equivalent for visible marks.

An empty `B` must always produce an invisible/unknown mark. Vacuous truth must not make an empty mark visible.

## Aggregate contract

Filtering does not end at visibility. For the selected facts `I`, representative measures are:

```text
total_tests  = sum(tests)
avg_download = sum(download_mbps × tests) / sum(tests)
avg_upload   = sum(upload_mbps × tests) / sum(tests)
avg_latency  = sum(latency_ms × tests) / sum(tests)
avg_share    = sum(sample_share) / count()
```

The serving representation should store distributive partials rather than finalized averages:

- count;
- sum;
- weight denominator;
- weighted numerator;
- minimum and maximum where required;
- bounded argmin/argmax category partials where required by the measure grammar.

Any index benchmark that produces only matching row IDs or a visibility bitmap proves identification, not
the complete product requirement.

## Floating-point exactness

Fact membership must be exact at authored numeric boundaries. Aggregate “exactness” needs a separate declared
arithmetic contract because floating-point addition is not associative.

The existing client/server fold obtains bit parity by sharing Float32 cell partials and accumulating them in
the same effective wider arithmetic. A new tree that sums facts in another association can differ in the last
bit even when it selected identical facts.

Before production approval, choose one:

1. fixed-point/integer partials where source precision permits it;
2. a canonical bake/runtime reduction tree;
3. wider deterministic accumulators with a documented final rounding rule;
4. an explicit numeric tolerance for inspection and tests, if the product accepts non-bitwise equality.

Counts, integer weights, category codes, and visibility bitmaps should remain bit exact.

## LOD and geometry contract

- Tile selection continues to use the immutable baked quadtree.
- Filtering never rebuilds the LOD tree or changes tile identities.
- Internal merged marks remain at their baked representative geometry.
- A mark is discarded when none of its represented facts survive; a surviving subset changes its measure,
  not its geometry.
- A result is addressed by `(version, tile_key, mki)`, not by one global geometry ID.
- A filtered-out mark neither renders nor participates in GPU picking.
- During parent/child refinement, every tile that can draw must have a result for the active filter before the
  cover swaps.

If the desired product instead reclusters surviving facts or changes representative geometry after every
filter, that is a different problem requiring dynamic LOD reduction and invalidates the current latency plan.

## Null, NaN, and boundary rules

The next fixtures must pin:

- inclusive lower and upper numeric bounds;
- open-ended ranges;
- `null`, NaN, positive infinity, and negative infinity in source facts;
- a range whose lower bound exceeds its upper bound;
- a range outside the complete domain;
- facts exactly on tile/grid seams;
- facts exactly on numeric range boundaries;
- marks emptied by categorical/temporal context before numeric filtering;
- real-versus-merged mark identity at `zreal == z`.

Recommended default: missing/non-finite predicate values fail a finite numeric range. Open ranges need an
explicit rule rather than inheriting JavaScript or SQL null behavior accidentally.

## Performance contract

Record these separately:

| Stage | Target or required report |
|---|---|
| Warm server compute: predicate + LOD mapping + reduction | p99 ≤ 50 ms for one interaction |
| Result serialization | measured separately |
| Network transfer | bytes plus floors at 25, 100, and 1,000 Mbit/s |
| Browser decode/GPU update/render | measured separately |
| Ordinary warm click-to-visible-result | p95 ≤ 100 ms under a declared payload/bandwidth contract |
| Cold map/tile activation | separate SLA; never folded into warm percentiles |
| 1/10/100 unrelated filters | response percentiles, service time, throughput, CPU-seconds |

“Sub-100 ms all around” is physically impossible without an output contract. For 583,511 marks:

- one visibility bit per mark is approximately 73 KB;
- one UInt8 style code is approximately 584 KB;
- one UInt16 code is approximately 1.17 MB;
- one Float32 value is approximately 2.33 MB.

At 100 Mbit/s, the Float32 column alone has an ideal transfer floor around 187 ms. A production SLA must say
whether interaction returns only visibility/style codes and fetches exact inspection values on demand, or
requires a numeric value for every mark.

## Integration surface

- Tile/LOD selection: [`tiling.ts`](../../../web/src/features/map-viewer/engine/tiling.ts).
- Current fold input includes loaded tiles and retained stand-ins:
  [`MapViewerPage.tsx`](../../../web/src/pages/maps/MapViewerPage.tsx).
- Current real-versus-merged mark mapping and `mki` ordering:
  [`DuckDbFoldExecutor.cs`](../../../src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs).
- Current per-tile remote result directory:
  [`remoteFold.ts`](../../../web/src/features/map-viewer/engine/remoteFold.ts).
- Current GPU discard behavior:
  [`colorScaleExtension.ts`](../../../web/src/features/map-viewer/engine/colorScaleExtension.ts).
- Frozen measure grammar and partial semantics:
  [`measures.ts`](../../../web/src/features/map-viewer/engine/measures.ts) and
  [`Measure`](../../../src/Colossus.Domain/Measures).

## Risks

- A benchmark can accidentally identify geometry marks rather than the marks actually rendered at an
  internal LOD.
- A mark-level bitmap intersection can pass parity on correlated data while remaining logically incorrect.
- New aggregate association can select correct facts but fail current bitwise parity.
- Returning only visibility can meet the filter target while leaving color/inspection recomputation unsolved.
- Dynamic LOD semantics could be assumed later unless the immutable-tree contract is explicitly accepted.

## Acceptance evidence

- Owner-approved semantics for `ANY`, optional `ALL`, aggregate population, nulls, and boundaries.
- Cross-language fixtures run through brute force, the current DuckDB oracle, Lance + DataFusion, any custom
  aggregate provider, and the browser fold.
- Leaf and internal-LOD fixtures demonstrate different `mki` mappings for the same fact.
- A byte-level or declared-tolerance aggregate parity test.
- The performance dashboard reports all stages and cold/warm states separately.

## Next action

Resolve the owner decisions above and extend the shared fixtures before interpreting the Lance + DataFusion
experiment as a product result.
