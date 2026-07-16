# Slab companion format (v1) — cross-language authority

Normative spec for the R1 fact-companion slab. This is the **fourth cross-language authority** alongside
tiling, schema, and the measure grammar: the C# bake writer
([`SlabCompanionWriter`](../../src/Colossus.Infrastructure/Tiles/SlabCompanionWriter.cs)) and the TS
client reader/fold ([`web/src/lib/slab.ts`](../../web/src/lib/slab.ts)) both implement exactly what is
below, and both are pinned by the shared fixture [`tests/fixtures/slab-cases.json`](../../tests/fixtures/slab-cases.json).

Fold semantics and the measure grammar are **unchanged** (VIEW_CONFIG §1/§4). The slab is a *physical*
re-encoding of the same fact partials; a fold over it produces byte-identical results to the row-form fold.
Nothing here is data-shape-specific: axes, layout, cell order, and types are all derived from measured
grain and occupancy, never authored, and no rule names a channel or a dataset.

## 1. Axis model

Each companion grain channel is one **axis**. Two kinds, distinguished by filter algebra (VIEW_CONFIG):

- **categorical** — a dict channel filtered by equality. Domain = the canonical dictionary order
  (`manifest.channelDomains[name].values`). Cardinality = domain length. A context selects one code (v1).
- **ordered** — a temporal/date channel (or a future binned-numeric `range` channel) filtered by range.
  Domain = the ordered bin list (sorted distinct values), recorded in the manifest. Cardinality = bin
  count. A context selects a contiguous `[lo, hi]` bin span.

A value not in an axis domain matches nothing (the fold empties the affected marks).

## 2. Cell space and canonical order (R5 normative)

The cell space is the axis cross product; `cells = ∏ cardinality`. The **canonical cell enumeration orders
the ordered axis fastest (innermost, stride 1), categorical axes slower (outer)** — so an equality
selection is one contiguous run per categorical position and a range selection a contiguous sub-run
within it. `cellId = Σ_axis code_axis · stride_axis`, where each axis's stride is the product of the
cardinalities of all axes inner to it. With one categorical (card `C`) + one ordered (card `T`):
`cellId = catCode · T + orderedBin`, `cellId ∈ [0, C·T)`.

Ties among several categorical axes break by grain-channel order (outer = earlier channel). With several
ordered axes, exactly one is chosen cumulative (§4, recorded per-axis); all ordered axes are inner to all
categorical axes. `manifest.companionSlab.axes` lists axes in cell order (fastest last) so the client
derives identical strides.

## 3. Layout choice (measured, per view, at bake)

Occupancy `= nnz / (Σ_leaves marks · cells)` where `nnz` = total grain cells (facts at grain). Measured at
bake; recorded in `manifest.companionSlab.layout` and the bake log.

- `occupancy < 0.5` → **sparse (CSR)** — the reference views (measured 37.8 %).
- `occupancy ≥ 0.5` → **dense (cell-major, cumulative)**.

The client branches on the recorded layout, never on data shape.

## 4. Physical layouts

Both carry, per partial, the same logical plane over `(cell, mark)`; `mark` is the tile-local mark index
(the row-form `mki`, `0 … markCount−1`). Partial names and values are exactly the row form's
(`MeasurePartials`): `sum__<ch>`, `cnt`, `swp__<ch>__<w>`, `min__<ch>`, `max__<ch>`; `cnt` is Int32, the
rest Float32 — lossless, unchanged.

### 4a. Sparse (CSR)

Per tile, mark-major:

- `offsets` — `Int32[markCount+1]`, `offsets[0]=0`, `offsets[markCount]=nnz`. Mark `m`'s entries are
  `[offsets[m], offsets[m+1])`. (This replaces the row-form `mki` column entirely.)
- `cellIds` — `UInt8|UInt16|UInt32[nnz]`, width by cell count (`≤256`→u8, `≤65536`→u16, else u32). Entries
  within a mark are ascending by cellId (canonical order).
- one array per partial — `Float32|Int32[nnz]`, parallel to `cellIds`.

Not cumulative: the fold scans a mark's entries. Sparse opts out of cell-run slicing (its blocks are small);
it still gets plane splitting (§5).

### 4b. Dense (cell-major, cumulative)

Per tile, one plane per partial, cell-major: `plane[cell · markCount + mark]`. A whole **cell row**
`[cell · markCount, (cell+1) · markCount)` (all marks at one cell) is contiguous — the R5 slice unit and
a GPU texture row (gpu-residency §5.3–5.4), uploadable without reshape.

- **Subtractable** partials (`sum`, `cnt`, `swp`) are **cumulative along the (one chosen) ordered axis**
  within each categorical run: `plane[(cat·T + b)·M + m] = Σ_{b'≤b} raw[(cat·T + b')·M + m]`. A range fold
  `[lo, hi]` reads two cell rows: `cum[cat·T + hi] − cum[cat·T + (lo−1)]` (the `lo−1` term is 0 when `lo=0`).
- **Non-subtractable** partials (`min`, `max`) are **raw** (never cumulative) and scanned over `[lo, hi]`.
- Empty cells: additive partials store `0`; `min`/`max` store `NaN` (read as "no value"). Marks with no
  surviving fact finalize to `NaN` / `ARGMAX_UNKNOWN`, exactly as the row form.

No `offsets`/`cellIds` (every cell is present). `markCount` = the tile's mark count; `cells` from the axes.

## 5. Block container, pack directory, plane split (R2 + R5)

The slab is Arrow IPC (managed `Apache.Arrow` writer — RULES R3; the nanoarrow extension segfaults on
DuckDB.NET 1.5.3), but split into **independently gzip-compressed blocks**, one per plane plus, for sparse,
one structure block. Each block is a single-row Arrow message whose columns are `List<T>` (the child buffer
*is* the plane's typed array — zero-copy on the client). Block order in the pack: structure first, then
partials in `MeasurePartials` order.

- `@idx` (sparse only): `offsets` + `cellIds`.
- `<partialName>`: that plane's values.

`manifest.companionPack` (extended; `format: "slab"`):

- `entries[tileKey] = [offset, length]` — the tile's whole region (all its blocks). Whole-tile fetch is
  this one range; the client then splits it by `planeEntries`. Keeps the R2 fetch path and the verifier
  working. **All** companion tiles (leaf *and* internal) are packed — internal companions are no longer
  per-tile files, which is what compresses them (R5 internal compression) and unifies the fetch path.
- `planeEntries[tileKey] = { "@idx": [off,len], "<partial>": [off,len], … }` — absolute pack offsets per
  block, for plane splitting: a fold fetches `@idx` (sparse) plus only the planes its active measures need,
  coalescing adjacent ranges. Absent → whole-tile fetch (older bakes, or a non-split client).

`manifest.companionSlab` records `layout`, `axes` (name, kind, cardinality, `cumulative`, domain ref), the
partial list with types, and `cells`. Row-form bakes have no `companionSlab`; the client keeps the row
path (backward compatible, manifest-gated).

## 6. Fold equivalence (frozen)

The slab fold reproduces the row-form fold byte-for-byte for every measure and context. Finalization
(`avg`/`wavg` ratios, `share` part/whole, `argmax`/`argmin`, empty-set → `NaN`/`ARGMAX_UNKNOWN`) is the
shared `measures.ts` code, unchanged. The two paths differ only in how partials are accumulated per mark:

- **Sparse**: compile the context to a per-cell predicate once — a `Uint8[cells]` mask (categorical
  positions selected by equality, ordered bins selected by range) — then scan entries, adding each passing
  entry's partials to its mark's accumulators. `O(nnz)`, no per-row key decode.
- **Dense**: for each mark and each selected categorical position, a cumulative range fold is two indexed
  reads (`O(1)` in the ordered range width); `min`/`max` scan `[lo, hi]`. Indexed, not scanned.

## 7. Witness (verifier)

`Σ cnt == source rows`. When a `cnt` plane exists (a `count`/`avg` measure), the verifier sums it (dense:
the last ordered bin's cumulative cnt per (cat, mark); sparse: the `cnt` array). When it does not, the
witness is `Σ nnz` (sparse entry count) / `Σ` occupied cells — identical to today's row-count witness on
grain-unique sources, which the reference data is. Recorded per view in the verify output.

## 8. Types (narrow where lossless)

`offsets` i32; `cellIds` u8/u16/u32 by cell count; `mki` **eliminated** (sparse offsets / dense indexing —
strictly better than the row form's tile-local u16); partials f32/i32 exactly as the row form. No
quantization, sketches, or sampling.
