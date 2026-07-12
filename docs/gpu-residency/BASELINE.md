# Phase 0 — Baseline (abbreviated)

Measured before Phase 1, on this worktree at base commit `8bfdf18`. Re-measure the relevant rows
after each landed phase and append a column.

## Setup

- Views baked fresh: `geonames` (point, 13,447,008 rows → 197 tiles, 146 leaves, maxZoom 8) and
  `ookla-fixed` (polygon, 6,655,986 rows → 3596 tiles, 2373 leaves, maxZoom 8). `verify` = PASS.
- Dev servers via `.claude/launch.json`: tile server (`Colossus.Server`) on :5174, web (Vite) on :5212.
- Stress view for the table: `geonames`.

## Method notes / caveats (so the numbers are honest)

- **Tile fetch + decode happen inside the Web Worker pool** (`tileWorker.ts`), so main-thread
  `performance.getEntriesByType('resource')` and the preview network tool see **zero** `.arrow`
  entries even while tiles load. Fetch/decode counts below are therefore derived from the code
  mechanism (confirmed by observed behavior), not a main-thread resource count. Phase 1's acceptance
  ("0 `.arrow` fetches, 0 worker messages") is instrumented directly in the worker at that time.
- **Settle detection**: an in-page harness drives the real HUD `<select>` (native value setter +
  `change` event) and polls the HUD's rendered-cell readout (`in view: N tiles · M cells`) until it
  changes and then holds stable for 200 ms; reported ms subtracts that 200 ms window. This captures
  worker fetch + decode + GPU upload + paint end-to-end.
- The Browser-pane **screenshot** action times out on this WebGL/MapLibre canvas (a harness capture
  quirk — the app itself is fully responsive; `javascript_tool`, `read_page`, and DOM reads all work).
  Visual parity in later phases is checked via the rendered mark set (HUD counts + read_page) rather
  than pixel screenshots where screenshotting is unavailable.
- Recolor's frame-gap number is a loose upper bound (rAF can be throttled when the pane is
  backgrounded); treat it as "hundreds of ms to ~2 s stall", not a precise figure.

## Baseline numbers

| Metric | Value (before Phase 1) | How measured |
|---|---|---|
| Filter change → settled, coarse (4 aggregated tiles, 128k cells) | 26–393 ms, **p50 ≈ 82 ms** | in-page harness, feature_class → {P,A,S,L,H,(all)} |
| Filter change → settled, full-fidelity leaves (2 leaf tiles) | **~1.0–1.8 s** | same harness after zooming to leaves |
| Tile fetches per filter change | = **# visible tiles** (all miss cache) | mechanism: new `compositeKey(version, fkey, tile)` per filter → every visible tile is a cache miss → `tileLoader.load` refetch+redecode. HTTP-cache-served (immutable `max-age=31536000`), so cost is decode+copy+GPU re-upload, not network |
| Worker decode per filter change | one **whole-tile** decode per visible tile | `decodeTile` runs `rowsMatching` (O(n) predicate scan) + gather-copies every column/geometry buffer + triangle rebase; deletion targeted by Phase 1 |
| Recolor (color-channel switch) | **~hundreds of ms to ~2 s** main-thread stall (loose) | max rAF gap over 8 frames after driving the color-by `<select>`; 0 main-thread `.arrow` fetches (baked `channelDomains`), so it is pure CPU `markColors` rebuild + per-vertex RGB re-upload |
| JS heap delta across filter flips | ~1 MB at leaf zoom (small state) | `performance.memory` before/after; note the cache also holds a **duplicate tile per filter state** (composite key includes `fkey`), so heap grows with distinct filter selections at larger tiles |
| Resident cache | one entry **per (version, fkey, tile)** | `tileCache` keyed by `compositeKey`; Phase 1 collapses to one entry per (version, tile) |

## Mechanism baseline (what Phases 1–2 must beat)

1. **Filter change** = new `fkey` in the cache key ⇒ every visible tile misses ⇒ worker fetch
   (HTTP-cached) + full `decodeTile` (predicate scan + gather copies + triangle rebase) + new
   `TileData` identity + full GPU geometry re-upload. Cache also duplicates tiles per filter state.
2. **Recolor** = CPU `markColors` (per-mark scale eval → `vertexCount*3` RGB array per
   (channel, scaleKey)) + attribute re-upload. No refetch (geometry `data` identity is stable across
   recolor via `deckData` memo), but the color attribute is rebuilt and re-uploaded.
3. **Decode copies everything** (`readFields`/`readPolygons`/`readTriangles` copy out of Arrow) —
   Phase 3's target; unchanged by Phases 1–2.

Expected after Phase 1 (sanity target, not a promise): filter change < 16 ms (one frame), **zero**
`.arrow` fetches, **zero** worker messages, cache holds one entry per (version, tile).
