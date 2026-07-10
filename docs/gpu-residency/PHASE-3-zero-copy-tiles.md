# Phase 3 — Tile Format v2 + Zero-Copy Decode

**Goal:** a tile is **one contiguous ArrayBuffer** (the fetched Arrow IPC message) for its whole
client lifetime. Decode becomes header parsing + typed-array **views** into that buffer — no
column copies, no geometry slices, no triangle rebase. The only client-built arrays are the small
derived ones (`polyStartIndices`, `filterValues`, per-vertex expansions).

**Prerequisites:** Phases 1–2 landed. They removed the two reasons decode had to copy (filter
gathers, color arrays); what remains is mechanical.

**This phase has a bake half and a client half.** The bake half is gated behind a manifest field
`tileFormat: 2`; the client keeps the current copy-path for format-1 tiles until all views are
re-baked, then the fallback is deleted (owner's call, separate commit).

---

## 1. Why decode copies today (so you know what you're removing)

[tileData.ts `readFields`](../../web/src/lib/tileData.ts) copies every column out of the Arrow
table because holding a view pins the entire IPC message — including columns the mark never reads
— and multi-chunk / null-bearing columns can't be viewed at all. The fix is to make the message
itself the retained allocation and guarantee, at bake time, the properties that make views safe:

1. **Single record batch** — already true: `ArrowTileWriter.Flush` writes exactly one
   `RecordBatch` ([ArrowTileWriter.cs](../../src/Colossus.Infrastructure/Tiles/ArrowTileWriter.cs)).
   Format v2 makes it a *contract* instead of an accident.
2. **No nulls in any column** — nulls force `col.get(i)` object paths. v2 contract: the extract
   normalizes nulls (strings → the literal `'null'`, matching today's `String(null)` rendering;
   numerics → `NaN`, which Phase 2 already renders as the unknown color and Phase 1 filters
   correctly via the sentinel rules).
3. **Tile-global triangle indices** — today `triangles` are row-local
   ([PolygonTriangulator](../../src/Colossus.Infrastructure/Tiles/PolygonTriangulator.cs) output,
   rebased per-row on the client in `readTriangles`). v2 writes them already rebased, so the client
   takes a single view over the whole child buffer.
4. **Canonical dictionary order** — v2 writes every dictionary-encoded column's dictionary in the
   **same order as `manifest.channelDomains[channel].values`**, making tile-local codes equal to
   canonical codes. Phase 1/2's remap becomes an identity fast-path (skipped when `tileFormat ≥ 2`).
5. **Measures stored as Float32** — verify what the tile SQL emits per measure today (check the
   reducers' SELECTs and [ArrowColumnBuilder.cs](../../src/Colossus.Infrastructure/Tiles/ArrowColumnBuilder.cs)
   type mapping). Any `f64`/`i64` measure column cannot be viewed as f32; v2 casts measures to
   `REAL` in the tile SQL so the stored buffer IS the render buffer. (Wire size also halves for
   those columns.)

Columns the client doesn't read for a given mark (`x`/`y`/`part_offsets`/`merged_count` on polygon
tiles) **stay in the tile** — R3 mandates them, and retaining them inside the single buffer costs
~8–16 bytes/mark against geometry that costs hundreds. Do not remove columns; do not amend R3.

## 2. Bake half

### 2.1 Manifest

- `Colossus.Domain/Model/Manifest.cs` (find the manifest model actually serialized — follow
  `BakeViewUseCase`): add `TileFormat` (int), serialized as `tileFormat`, value `2`. Absent/0/1 on
  old manifests means format 1. Client type in `manifest.ts`: `tileFormat?: number`.

### 2.2 Writer changes ([ArrowTileWriter.cs](../../src/Colossus.Infrastructure/Tiles/ArrowTileWriter.cs))

- **Global triangles:** `TileBuffer` tracks a running vertex count (`coordsSoFar / 2`).
  `AppendTriangles` appends `PolygonTriangulator.Triangulate(...)` indices **plus the row's vertex
  start**. Update `TileSchema.Triangles`'s doc comment ("tile-global" instead of "row-local").
- **Non-nullable fields:** mark channel/geometry/triangles fields `nullable: false` in the schema
  when the builders guarantee it; keep `AppendRow` null handling as a bake-time error (throw with
  the column name) rather than a silent null — the extract normalization (2.3) makes this
  unreachable.
- **Canonical dictionaries:** `ArrowColumnBuilder` dictionary building currently assigns codes in
  first-seen order per tile. Add an optional `IReadOnlyList<string>` canonical order parameter,
  passed down from the reducers (which get it from the same channel-domain scan that fills
  `channelDomains` — see `BakeViewUseCase` for where domains are computed; **compute domains before
  tile writing if they aren't already**). Values outside the canonical list (domain truncated):
  fall back to per-tile appended codes *after* the canonical ones, and — important — leave
  `tileFormat` at 2 but mark that channel's domain `valuesTruncated`, which already makes the
  client fall back to remap (Phase 1 rules). Cheapest correct behavior, no new flags.

### 2.3 Extract normalization (no-nulls contract)

In the adapter SQL (follow `ISourceAdapter`/ClickHouse extract and the reducer SELECTs):
string/dimension/identity channels get `COALESCE(col, 'null')`; temporal and numeric channels pass
through (`NaN`/sentinel handling is client-side per Phases 1–2). Confirm DuckDB staging preserves
this. Add one bake test with a null-bearing source column asserting the tile has zero nulls and the
client renders it as the string `'null'` / unknown color — pin today's behavior exactly.

### 2.4 Bake tests

- Round-trip test (pattern: existing Arrow round-trip tests through in-memory DuckDB): write a
  polygon tile via `WritePartitioned`, read back, assert triangle indices are tile-global (max
  index < total vertex count; per-row min ≥ row's vertex start).
- Dictionary-order test: two tiles from one bake share identical dictionaries in canonical order.
- `dotnet run --project src/Colossus.Bake -- verify` against a fresh bake of every configured view.

## 3. Client half

### 3.1 Fetch keeps the buffer

`arrow.ts`: `fetchArrowTable` returns `{ table, buffer }` (the `ArrayBuffer` passed to
`tableFromIPC`). Callers that don't need the buffer ignore it.

### 3.2 Decode for format 2 (`tileData.ts`)

`decodeTile` gains the format switch (plumb `tileFormat` through the worker message next to
`slots`):

- **Views, not copies** — for every buffer that today is `.slice()`d or rebuilt:
  - `polyPositions`: `d.children[0].values.subarray(base, offsets[n])` — a view.
  - `polyTriangles`: `new Uint32Array(child.values.buffer, child.values.byteOffset + off[0] * 4, off[n] - off[0])`
    — a reinterpreting view over the Int32 child (indices < 2³¹, same bytes). No loop.
  - Numeric measure columns: `col.data[0].values` (Float32 guaranteed by 2.5's cast) — views.
  - Dict columns: codes buffer as a view; dictionary strings decoded once (small); **skip the
    canonical remap** when `tileFormat ≥ 2` and the channel's domain isn't truncated.
  - Utf8 identity columns: `bytes`/`valueOffsets` as views (offsets NOT rebased — store the base
    and adjust in `columnValue`, or keep the tiny `n+1` offset rebuild; choose the offset rebuild:
    it's `4(n+1)` bytes and keeps `columnValue` untouched).
  - Point `positions`: still built (interleave from separate `x`/`y` columns — unavoidable until a
    baked interleaved column exists; explicitly out of scope, noted in Phase 5).
- **Still built (small, derived):** `polyStartIndices` (`n+1` Uint32 from float-unit offsets,
  halved), `filterValues`, Phase 2 expansions.
- `TileData` gains `buffer?: ArrayBuffer` — the retention anchor. `transferable()` returns
  `[buffer, ...buffers of built arrays]`; views transfer along with their buffer automatically.
  Guard: never list the same ArrayBuffer twice (the `Set` already handles this).
- `tileBytes` for format 2: `buffer.byteLength` + built arrays' bytes (views must not be
  double-counted — check `.buffer === tile.buffer` when summing).
- **Format 1 path:** the current copy-based code remains, selected when `tileFormat < 2`. Keep both
  paths honestly separated (a top-level branch, not interleaved conditionals). The multi-chunk
  fallbacks live only in the format-1 path.

### 3.3 Tests

- Build format-2 fixture tables in `tileData.test.ts` (apache-arrow JS can author
  single-batch, non-null, dictionary tables in memory): assert view identity
  (`out.polyPositions.buffer === fetched.buffer`), triangle values, no-remap fast path,
  `tileBytes` no-double-count.
- Keep format-1 tests as-is (they now pin the fallback).

## 4. Acceptance criteria (all required)

1. All standard verification (README §6) green, including a **fresh bake + verify** of every view
   in the local registry.
2. **View residency proof:** for a format-2 tile, every geometry/measure/code TypedArray in
   `TileData` satisfies `.buffer === tile.buffer` (assert in a test; spot-check in DevTools).
3. **Decode time drop:** re-measure the baseline zoom-swap metrics; worker decode time for the
   GeoNames leaf tiles should drop noticeably (expect ~30–50%); no visual change.
4. **Pixel parity** against Phase 2's screenshots (same fixed-camera set).
5. **Mixed-format session:** a format-1 view and a format-2 view opened in the same session both
   render correctly (switch views back and forth; cache keys are version-scoped so no collision).
6. Heap check: 5 filter flips + 2 zoom round-trips → `usedJSHeapSize` returns to within noise of
   start (no growth from retained buffers beyond the cache budget).

## 5. Risks / fallbacks

- **apache-arrow internal layout access** (`col.data[0].values`, `valueOffsets`): already relied on
  today in the same file; version-pin risk unchanged. The format-2 fixture tests will catch a
  library upgrade breaking assumptions.
- **Alignment:** Arrow IPC pads buffers to 8 bytes — Float32/Int32/Uint32 views are always aligned.
  If a view constructor ever throws misalignment, the writer is emitting something unexpected —
  fix the writer, do not add a copy fallback silently.
- **DuckDB → Arrow type surprises** (e.g. a measure arriving as `DOUBLE` despite the cast): the
  format-2 decode should **throw loudly** on an unviewable column type, failing the bake's verify
  step, rather than degrade to copying. Contract violations must be visible.
- **`merged_count`/LOD internal tiles:** they flow through the same writer; the global-triangles
  change only touches polygon tiles (points have no `triangles` column). Confirm with the
  round-trip tests.
