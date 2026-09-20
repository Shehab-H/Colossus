# 04 — LOD integration contract

**Status:** Candidate — the invariants are known; the compact mapping representation still needs proof.

**Last verified:** 2026-08-23.

**Estimate:** 1–2 engineering days to prototype and prove two internal zooms, one leaf zoom, and a
parent/child transition on the real reference map.

## Motivation

The numeric engine does not merely answer “which source facts passed?” The renderer needs an answer aligned
to the immutable mark rows in every tile it may draw during the current frame. Those rows are addressed by
`mki`.

This distinction is essential because a source fact does not own one universal rendered-mark identity. At a
leaf zoom it may belong to a real geometry. At an internal zoom the same geometry may be merged into a
coarser grid cell. Therefore a global bitmap over real geometry IDs cannot correctly filter every LOD.

The contract in this item keeps the existing LOD tree immutable and translates one exact fact result into
the tile-local mark identities required by the current cover.

## Existing behavior that must be preserved

- Camera movement selects an immutable tile cover in
  [`tiling.ts`](../../../web/src/features/map-viewer/engine/tiling.ts).
- During refinement, the fold input includes both loaded desired tiles and retained parent/child stand-ins in
  [`MapViewerPage.tsx`](../../../web/src/pages/maps/MapViewerPage.tsx). A filter result must cover every tile
  that can actually draw, not just the final desired cover.
- Bake/runtime mark identity depends on zoom. The present remote fold chooses the real mark when `zreal <= z`
  and otherwise chooses a merged grid mark; it then assigns tile-local `mki` by stable mark ordering in
  [`DuckDbFoldExecutor.cs`](../../../src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs).
- Remote results are already represented as tile runs of `mki`-ordered columns in
  [`remoteFold.ts`](../../../web/src/features/map-viewer/engine/remoteFold.ts).
- A dead/unknown mark is fragment-discarded, including for picking, in
  [`colorScaleExtension.ts`](../../../web/src/features/map-viewer/engine/colorScaleExtension.ts).

The new numeric path should use these seams rather than invent a second LOD tree.

## Identity model

Use three separate identities and never treat them as interchangeable:

| Identity | Scope | Purpose |
|---|---|---|
| `fact_id` | one immutable map version | Authority for exact predicate membership and cross-index intersection. |
| `mark_code` | one tile/zoom mapping | Stable real or merged rendered-mark key before row numbering. |
| `mki` | one rendered tile | Dense row position used by tile columns, shaders, inspection, and picking. |

Example:

```text
fact 9012 at z=2  → merged mark_code M → tile 2/1/1, mki 17
fact 9012 at z=5  → real   mark_code R → tile 5/9/12, mki 8221
```

The fact passed only once, but it contributes to different rendered rows at the two zooms. This is why the
output key is `(tile_key, mki)`, not only a geometry ID or only an `mki`.

## Required runtime pipeline

```text
camera-selected drawable tile keys
→ exact numeric fact predicate
→ translate each surviving fact to a rendered mark at each requested tile/zoom
→ group additive partials by (tile_key, mki)
→ encode per-tile visibility and requested value columns
→ update the existing tile render state
```

The numeric query may prune spatially first or numerically first. Correctness does not depend on that plan
choice, provided the final fact set is intersected with the currently drawable tiles and translated with the
same mark rules used at bake.

## Recommended compact representation

The first proof should store each fact once with:

- `fact_id`;
- a high-resolution integer spatial code or fixed-point coordinates;
- stable real-mark ID/code;
- `zreal`;
- numeric predicates;
- additive aggregate partials.

It should separately persist a compact tile mark directory:

```text
(tile_key, mark_code) → mki
```

For a requested zoom, integer prefix/bit operations derive the tile and merged-cell code from the fact's
high-resolution spatial code. The `zreal` rule chooses real versus merged `mark_code`; a lookup in the tile
directory produces the exact baked `mki`.

This design avoids duplicating all numeric columns at every LOD. It also avoids request-time floating-point
geometry operations, string key construction, sorting, and `row_number()` assignment. Those expensive
operations belong at bake time.

### Representation to avoid first

A materialized row for every `(zoom, tile, fact_id, mki)` is easy to query but repeats fact membership for
every LOD. If a fact participates at roughly 15 zooms, even a compact 8-byte membership becomes about 120
bytes per fact before indexes and payloads: approximately 12 GB at 100 million facts and 120 GB at one
billion. It remains a useful correctness oracle for a small fixture, not the preferred serving layout.

### Deferred alternative

A lineage tree can store a real mark's parent merged mark at each boundary and walk ancestors to the current
zoom. That may compress unusually sparse/hierarchical data better, but it adds pointer chasing and more
complex transition logic. Test the integer spatial-code plus tile-directory design first.

## Filter semantics at the LOD boundary

### `ANY`

A rendered mark is visible when at least one fact mapped to that `(tile_key, mki)` satisfies all active
numeric predicates and the existing categorical/temporal context.

Its aggregates are reduced from only those matching facts.

### `ALL`

A rendered mark is visible only when every fact in that rendered mark satisfies all active numeric
predicates. The min/max header can answer this without scanning only if the header was built for that exact
rendered mark and base context. A leaf real-mark header cannot automatically answer for an internal merged
cell; the internal cell needs its own extrema or a correct composition from descendants.

### Empty results

Every requested tile still receives an explicit valid result version containing an empty visibility bitmap.
Absence of a response must not be interpreted as “show the old result.”

## Aggregate contract

Group by `(tile_key, mki)` and combine the same additive partials used by the existing measure fold. Visibility
alone is insufficient when color, size, labels, or inspection depend on the filtered population.

For every configured measure, item 01 must classify it as:

- directly additive (`count`, `sum`);
- algebraic from additive partials (`average = sum/count`);
- mergeable with a declared state;
- unsupported for exact interactive filtering.

The LOD translation must never aggregate a fact twice when parent and child tiles coexist during refinement.
They need separate outputs because both may draw, but each tile result reduces its own population exactly
once.

## Response format

Return one versioned result per requested tile:

- tile key and immutable map/version ID;
- mark count used to validate alignment;
- visibility bitmap indexed by `mki`;
- only the style/measure columns required for the active view;
- optional counts/checksums for diagnostics.

A bitmap costs one bit per mark: 583,511 marks require about 73 KB before protocol overhead. A Float32 value
for every one of those marks costs about 2.33 MB. Therefore the response contract should prefer bitmaps,
compact palette/category codes, and on-demand inspection over returning every possible measure every time.

On the client, the long-term target is one visibility/value buffer or texture per tile, indexed by `mki`.
The initial proof may continue to use the existing unknown/NaN discard seam if that keeps the experiment
small, but it must verify picking as well as visible pixels.

## Consistency and cancellation

- Every request carries map version, filter revision, and requested drawable tile keys.
- Results from an older filter revision are discarded even if they finish later.
- A tile loaded after the request either receives the current revision from cached server state or triggers a
  query for that tile; it must not silently appear unfiltered.
- Parent/child stand-ins remain filtered until the renderer stops drawing them.
- The server validates that returned mark counts and `mki` order match the immutable tile manifest.

## Proof fixture

Construct a small deterministic fixture containing:

1. two real marks that merge into one internal cell;
2. one real mark that stays real at an earlier zoom due to `zreal`;
3. facts on inclusive range boundaries;
4. one mark whose different facts satisfy different dimensions, proving mark-level bitmap intersection is
   wrong;
5. a parent/child transition in which both tiles draw;
6. an empty query and a query that includes all facts.

Compare the compact translation against a simple materialized `(zoom, tile, fact_id, mki)` oracle, then run
the same comparison over sampled and adversarial ranges on the full real map.

## Integration surface

- Tile selection: [`tiling.ts`](../../../web/src/features/map-viewer/engine/tiling.ts).
- Drawable fold input and stale/dead handling:
  [`MapViewerPage.tsx`](../../../web/src/pages/maps/MapViewerPage.tsx).
- Current real-versus-merged and `mki` ordering oracle:
  [`DuckDbFoldExecutor.cs`](../../../src/Colossus.Infrastructure/Fold/DuckDbFoldExecutor.cs).
- Existing per-tile column transport:
  [`remoteFold.ts`](../../../web/src/features/map-viewer/engine/remoteFold.ts).
- GPU discard and picking behavior:
  [`colorScaleExtension.ts`](../../../web/src/features/map-viewer/engine/colorScaleExtension.ts).
- Static mark key rules: [`MarkKey.cs`](../../../src/Colossus.Infrastructure/Tiling/MarkKey.cs).

## Risks

- A mismatch between bake-time and runtime integer spatial encoding will create plausible but wrong `mki`
  assignments.
- Internal `ALL` headers can be large if duplicated for every rendered cell and base context.
- Random tile-directory lookups may dominate after the predicate engine becomes fast; rows should be
  partitioned and ordered to make translation sequential where possible.
- Parent and child results can flash or double-render if filter revision and tile-cover revision are confused.
- A bitmap-only implementation may look correct while returning stale inspection values or incorrect picks.

## Acceptance evidence

- Exact parity with the materialized oracle for all fixture cases.
- Full real-map parity at a leaf zoom and at least two internal zooms.
- The same visible result during a forced parent/child transition, with no unfiltered frame.
- Correct color/style aggregates, inspection, and picking for dead and live marks.
- Measured mapping time, directory bytes, response bytes, and peak RSS reported separately from predicate
  time.
- A storage projection to one billion facts that does not multiply numeric facts by the number of zooms.

## Next action

Build the oracle fixture first, then include the compact mapping columns and tile directory in
[item 05](05-production-shaped-lance-datafusion-proof.md). Do not benchmark fact predicates alone and call
the result an end-to-end filter.
