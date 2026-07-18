# Slab companion format (v2) — cross-language authority

Normative spec for the R1 fact-companion slab. This is the **fourth cross-language authority** alongside
tiling, schema, and the measure grammar: the C# bake writer
([`SlabCompanionWriter`](../../src/Colossus.Infrastructure/Tiles/SlabCompanionWriter.cs)) and the TS
client reader/fold ([`web/src/lib/slab.ts`](../../web/src/lib/slab.ts)) both implement exactly what is
below, and both are pinned by the shared fixture [`tests/fixtures/slab-cases.json`](../../tests/fixtures/slab-cases.json).

> **v2 (2026-07-18, companion-scale R5 second half).** The dense/sparse gate is **per leaf tile**, not per
> view (§3, `companionSlab.tileLayouts`); a dense plane is stored as **one compressed block per cell row** so
> the client fetches only the rows a context reads (§4b/§5, `companionPack.sliceEntries`); and the pack codec
> is **zstd + a trained shared dictionary** (§5, `companionPack.codec`/`dict`/`dictHash`). All three are
> manifest-gated — absence selects the v1 behaviour (per-view layout, whole-plane fetch, gzip), so v1 bakes
> still load. Fold results are byte-identical across versions.

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

**Axis values are recorded in one canonical spelling (normative).** The domain, the `cellId` mapping, and
the client's context compare must all agree on it, or a fold silently resolves empty. A temporal axis's
canonical form is **ISO `YYYY-MM-DD`** — the form `tests/fixtures/slab-cases.json` pins and the client's
range compare needs (lexical order on ISO is chronological). The bake normalises to it whatever the adapter
delivered: a `DATE`, an integer day count (ClickHouse's `Date` extracts as one), or epoch millis — the same
day-count vs epoch-millis split `web/src/lib/dates.ts` uses. The conversion follows the channel's declared
temporal role, never its name (data-agnostic).

> Fixed 2026-07-17 (found while building R4's parity gate): the domain was scanned with a bare
> `CAST(col AS VARCHAR)`, so a ClickHouse `Date` recorded bins as `'19723'` while contexts arrived as
> `'2025-01-01'`. Every range fold resolved `lo > hi` → *impossible* → the whole map went unknown. The
> fixture never caught it because its facts are already ISO strings; only a real bake hit the integer path.

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

## 3. Layout choice (measured **per leaf tile**, at bake)

Occupancy `= nnz / (marks · cells)` where `nnz` = the grain-cell count (facts at grain). The gate is applied
**per tile** from that tile's own `nnz` and `marks`, not once for the whole view:

- `occupancy < 0.5` → **sparse (CSR)**.
- `occupancy ≥ 0.5` → **dense (cell-major, cumulative)**.

Skewed spatial density means one view holds tiles of both kinds; the writer chooses each independently and the
reader/decoder branches per tile. Data-agnostic: it is the same measured gate, applied to the tile.

**Recording the choice.** `manifest.companionSlab.layout` is the **view default** — the layout picked from the
view's *global* occupancy (`nnz / (Σ_leaves marks · cells)`), and the layout an older client that ignores the
per-tile field would use for every tile. `manifest.companionSlab.tileLayouts` is an optional map
`tileKey → "dense"|"sparse"` recording **only the tiles whose own layout differs from the default** (skew makes
those the minority, so the map is small). A tile's layout is:

```
layoutOf(tileKey) = companionSlab.tileLayouts?[tileKey] ?? companionSlab.layout
```

Absent `tileLayouts` (a uniform view, or a bake predating per-tile choice) ⇒ every tile is the default —
backward compatible. The client, which must know a tile's layout *before* it fetches (sparse fetches the `@idx`
structure, dense fetches `cnt`; §5), reads this field; the C# reader can instead read it physically — a sparse
tile carries an `@idx` block, a dense one never does (§5) — and the two always agree.

**cnt is always present.** A dense tile needs a `cnt` plane for both survival (a mark survives a range iff its
cumulative `cnt` over the range is > 0) and the witness (§7). Because the gate is per tile and tiles are
streamed, any tile may go dense, so the partial set always includes `cnt` even when no declared measure needs
it. Sparse tiles carry it too — the witness prefers `Σ cnt` (correct even when a grain cell holds several source
facts) to `nnz`, and a near-all-ones `cnt` plane compresses to almost nothing. The fold still reads only the
planes its measures need.

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

A dense plane is stored **one independently compressed block per cell row** (§5), not one whole-plane block,
so the client can fetch just the rows a context reads (R5 cell-run slicing). The blocks are **raw
little-endian typed-array bytes** (f32, or i32 for `cnt`) — *not* Arrow: per-row Arrow framing would swamp a
small tile's payload, and the row's element type is already known from the partial. Their concatenation, in
cell order, is the whole plane; the C# reader inflates the whole region in one pass (`GZipStream` reads the
concatenated gzip members), the client inflates only the rows it fetched.

## 5. Block container, pack directory, plane split + cell-run slice (R2 + R5)

The slab is split into **independently compressed blocks**. A **sparse** tile's blocks are Arrow IPC (managed
`Apache.Arrow` writer — RULES R3; the nanoarrow extension segfaults on DuckDB.NET 1.5.3): one structure block
plus one per plane, each a single-row Arrow message whose columns are `List<T>` (the child buffer *is* the
plane's typed array — zero-copy on the client). A **dense** tile's blocks are **raw little-endian cell-row
blocks** (§4b) — one per cell row per plane, no Arrow framing. Block order in the pack: structure first, then
partials in `MeasurePartials` order (a dense partial's cell rows in cell order).

- `@idx` (sparse only): `offsets` + `cellIds`.
- `<partialName>`: that plane's values — one block (sparse), or `cells` cell-row blocks (dense).

`manifest.companionPack` (extended; `format: "slab"`):

- `entries[tileKey] = [offset, length]` — the tile's whole region (all its blocks). Whole-tile fetch is
  this one range; the client then splits it by `planeEntries`. Keeps the R2 fetch path and the verifier
  working. **All** companion tiles (leaf *and* internal) are packed — internal companions are no longer
  per-tile files, which is what compresses them (R5 internal compression) and unifies the fetch path.
- `planeEntries[tileKey] = { "@idx": [off,len], "<partial>": [off,len], … }` — absolute pack offsets per
  plane region, for plane splitting: a fold fetches `@idx` (sparse) plus only the planes its active measures
  need, coalescing adjacent ranges. A dense plane's region spans all its cell-row blocks. Absent → whole-tile
  fetch (older bakes, or a non-split client).
- `sliceEntries[tileKey] = { "<partial>": [len₀, len₁, … len_{cells−1}], … }` — **dense tiles only** (R5
  cell-run slicing): each plane's per-cell-row compressed block lengths. Cell `c`'s block sits at
  `planeEntries[tile][plane][0] + Σ_{i<c} lenᵢ` for `len_c` bytes, so only lengths are stored (offsets are the
  prefix sum). Absent (or a sparse tile) → whole-plane fetch. Sparse opts out: its blocks are already small
  and CSR is mark-major, hostile to cell slicing (REQUIREMENTS R5).

**Cell-run slice fetch (dense, client).** From the active context the client compiles the exact cell rows the
fold reads (`denseNeeds`, mirroring §6's dense reads): for each passing categorical run, a cumulative plane
needs the `hi` and (when `lo>0`) `lo−1` rows, a `min`/`max` plane the `[lo..hi]` run; `cnt` (survival) is
always among them. It fetches only those cell-row blocks — coalescing adjacent ones into a single HTTP Range,
parallel ranges otherwise — inflates each, and assembles a plane array in which only the fetched rows carry
values (the fold never reads the holes). Slices cache **under the tile key** (`(version, tileKey)` stays the
only data identity — RULES R4); a later context that needs more rows fetches just the delta and merges it in.

`manifest.companionSlab` records `layout`, `tileLayouts` (per-tile overrides, §3), `axes` (name, kind,
cardinality, `cumulative`, domain), the partial list with types, and `cells`. Row-form bakes have no
`companionSlab`; the client keeps the row path (backward compatible, manifest-gated).

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

`Σ cnt == source rows`. A slab bake always carries a `cnt` plane (§3), so the verifier sums it per tile under
that tile's layout — dense: the last ordered bin's cumulative cnt per (cat, mark); sparse: the `cnt` array. (A
row-form bake, or an older slab without `cnt`, falls back to `Σ nnz` / `Σ` occupied cells — identical to the
row-count witness on grain-unique sources, which the reference data is.) The C# reader resolves the per-tile
layout from the `@idx` block's presence, so a mixed-layout pack witnesses correctly tile by tile. Recorded per
view in the verify output.

## 8. Types (narrow where lossless)

`offsets` i32; `cellIds` u8/u16/u32 by cell count; `mki` **eliminated** (sparse offsets / dense indexing —
strictly better than the row form's tile-local u16); partials f32/i32 exactly as the row form. No
quantization, sketches, or sampling.
