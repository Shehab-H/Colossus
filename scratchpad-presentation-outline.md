# Colossus — Technical Introduction (Deck Outline, v4)

**Audience:** mixed technical team (engineers + PM/design)
**Length:** ~20 slides
**Structure:** problem → solution chains. No slide presents a file format as a fact; every structure
and encoding appears as the answer to a concrete problem — and most solutions create the next
problem, which is the next slide. Numbers marked *measured* come from our reference bakes.

---

## Section A — The Problem (slides 1–4)

### Slide 1 — The problem
- Tens of millions to billions of rows — speed tests, coverage cells, buildings — and people who
  need to *see* and *interact* with them: pan, zoom, filter, click a mark, ask "why is this one red?"
- Three wants that pull against each other: every mark is a real row · interaction feels instant ·
  serving doesn't need a compute cluster per viewer.

### Slide 2 — Why the sizes are brutal (back-of-envelope)
- 100M marks × ~30–40 B (position + a few channels) ≈ **3–4 GB** before geometry — and polygon
  geometry is 69–99% of a polygon tile's bytes.
- No browser holds that; no network ships it per page load; no GPU draws it naively at 60 fps.
- Something must give: what you show, when you ship it, or what each interaction costs.

### Slide 3 — What existing tools give up
Each is good at its job; none gives "every real mark, interactive, cheap to serve":
- **Live BI (Superset/Tableau/Grafana):** every pan/filter is a `GROUP BY` round-trip. Aggregates,
  not marks; latency is a query; cost scales with viewers.
- **Vector map tiles (Mapbox-style):** built for cartography — they simplify and *drop* features
  per zoom. Right for roads; wrong when each feature is a data point someone will inspect.
- **Server rasters (datashader-style):** the server draws an image. Faithful density, but marks are
  gone — no client filter, no click, every interaction a server render.
- **Sampling:** fast, but unlabeled samples quietly lie, and usually can't be resolved to the full
  data by zooming.

### Slide 4 — The bet
- **Do the heavy work once, at bake** (batch cost, per dataset version). **Serve static bytes**
  (a file server with Range support is the backend). **Make interaction GPU state.**
- Honest costs: data is a snapshot (re-bake to update); bakes take real CPU time; storage is spent
  deliberately to save wire and compute later.
- The rest of the talk: the chain of problems this bet runs into, and the structure each one forced.

---

## Section B — Bake: each problem forces a structure (slides 5–11)

### Slide 5 — Problem: you can't ship 100M rows. Which rows go where?
- **The obvious fix** — "just tile it" — immediately splits into three cases the data itself
  distinguishes:
  - source already fits a tile → ship it whole (RawPassthrough);
  - many rows per shape (a fact cube: operators × quarters over coverage cells) or an area mark →
    an aggregate pyramid whose coarse cells are honest means of children;
  - a genuine point cloud → a quadtree pyramid whose coarse tiles are a *labeled preview* that
    resolves as you zoom.
- Nobody authors the choice — a probe (row count, distinct geometries, bbox) decides. Budgets are
  concrete: 250k marks per leaf, a 512×512 grid per tile (≈1 grid cell per screen pixel).
- **The invariant that survives all three:** every source row lands in exactly one leaf, once —
  a verifier asserts `Σ tiles == source` on every bake.

### Slide 6 — Problem: a point on a tile boundary. Whose tile is it?
- Naive edge math (`min + i*cell`, computed independently per tile) rounds twice — a seam point can
  satisfy two adjacent tiles, or neither. At 100M rows, "can" means "does."
- **Solution:** one edge function over power-of-two divisions, single rounding — a tile's max edge
  is *bit-for-bit* its neighbour's min edge; half-open intervals make ownership exact.
- **The problem behind the problem:** this math must give identical answers in three places —
  C# (bake), generated DuckDB SQL (reducers), TypeScript (client tile selection). One drifts and
  marks silently vanish on seams. **Solution:** one authority, mirrors pinned by a shared JSON
  fixture that all three test suites run. (This "authority + fixture" pattern recurs — count them
  through the talk; there are four.)

### Slide 7 — Problem: sources disagree about what geometry even is
- lon/lat points, x/y points, quadkeys, WKT polygons — if source shape leaks past ingest, every
  downstream stage grows per-source branches forever.
- **Solution:** normalize at extract into one canonical shape, and give *every* feature —
  point or polygon — a representative `(x, y)`. That one trick lets a single set of spatial
  machinery (sorting, tiling, viewport query, LOD) treat all marks identically.
- Each geometry kind is one small SQL-generating class; adding H3 touches nothing downstream.

### Slide 8 — Problem: the client has to *decode* what you ship
- Ship JSON or Parquet and the client parses/decodes per cell into JS objects — GC churn, main
  thread jank, and then you *still* have to build the typed arrays the GPU wants.
- **Solution:** Arrow IPC — the column buffers in the file *are* the typed arrays deck.gl uploads.
  Reading a tile is essentially a memcpy.
- **But** naive Arrow still forced copies: nullable columns (validity bitmaps), multiple record
  batches, per-tile dictionary codes needing remap, per-row triangle indices needing rebase.
  **Solution (format 2):** make zero-copy a *contract* — one batch, no nulls (strings coalesce,
  measures → NaN), dictionaries written in canonical order, triangles rebased tile-global at bake.
  The client takes views over the one fetched buffer. Old bakes keep the copy path; the manifest gates.

### Slide 9 — Problem: polygon tiles are ~70–99% geometry bytes — and the geometry is boring
- Grid-cell polygons (quadkeys) are all axis-aligned rectangles; their triangles follow a pattern;
  coordinates repeat along shared edges. We were shipping megabytes of the derivable.
- **Solution (format 3):** encode geometry into one binary payload, decoded by a worker back to the
  *exact* format-2 buffers, bit-for-bit. Two codecs, chosen per tile from the data: corner-table
  indices for rectangles, delta+zigzag+byte-transposed coordinate streams for real rings.
  Measures and dictionaries stay untouched, still zero-copy.
- Bit-for-bit equality is testable — the codec is authority #2, pinned C# ↔ TS by a fixture.

### Slide 10 — Problem: the wire is still the bottleneck. Two answers, two constraints.
- **Whole-tile fetches:** precompress at bake — brotli at max settings (quality 11, 16 MB window),
  served as `Content-Encoding: br`; the browser decodes in the network stack, client code unchanged.
  ~5.5× on large tiles (*measured*), ~23 s CPU for the worst tile — a batch cost. Nothing compresses
  on the request path, ever.
- **But** the next slides need *partial* reads — and `Content-Encoding` does not compose with HTTP
  Range. **Solution:** wherever we range-read, compression moves *inside* the archive: blocks
  compressed individually at bake, fetched by byte range, inflated in the worker. This one
  constraint shapes every archive that follows.

### Slide 11 — Problem: you ship columns nobody is reading
- The lossless floor: f32 measure planes barely compress. The only remaining win is not *sending*
  them until an interaction reads them. A first paint needs geometry + the active colour channel —
  not five other measures.
- **Solution:** split tiles into per-column blocks in one archive; a first paint fetches one
  contiguous range because the block order is deliberate (geometry → default colour → filter slots
  → the rest).
- **New problem:** thousands of small blocks compress terribly alone. **Solution:** train a shared
  zstd dictionary on sampled blocks at bake (two passes: raw scratch → transcode); ship the
  dictionary once. (Opt-in per bake today; the client reads it.)

---

## Section C — Bake, group regime: measures create a second dataset (slides 12–13)

### Slide 12 — Problem: filters must *recompute* values, not just hide marks
- Coverage view: each cell's colour is a measure over its facts (operator × quarter × KPI). Filter
  to "Q3, operator A" and every visible cell's value must recompute over surviving facts — without
  a database round-trip (interaction!) and without shipping raw facts (100M of them).
- **Solution:** bake partial aggregates per `(mark, grain cell)` — sums, counts, mins — enough to
  fold any filter context client-side. These *companions* are a second dataset riding beside the
  tiles.
- **New problem:** the obvious encoding (one row per (mark, cell): key columns + partials) costs
  ~28–36 B per fact; a dense leaf tile is tens of MB, and half of it is repeated key material.

### Slide 13 — Problem: the companion's keys are the bytes. Make them implicit.
- **Solution (slabs):** enumerate the grain cross-product in canonical order and store one plane
  per partial over `cells × marks` — keys become array indices. Fold is O(1) indexed lookup,
  vectorizable, GPU-uploadable.
- **Chain of follow-on problems, each forcing a refinement:**
  - Range filters would scan cells → make ordered-axis planes *cumulative* (prefix sums); a
    date-range fold becomes two slice subtractions.
  - Sparse tiles (2% occupancy) would waste dense planes → measure occupancy per tile at bake;
    dense or CSR chosen per tile, recorded in the manifest, never authored.
  - A filter reads a sliver but fetches whole planes → a per-plane, per-cell-row directory; the
    client compiles the active filter to the exact rows the fold reads and fetches only those.
    *Measured, worst reference tile: 50.5 MB → 7.8 MB per interaction, ≥5× more from cell-run slicing.*
  - Some view could still exceed a client budget → the planner prices the fold against 32 MB; over
    budget, one endpoint runs the *same fold* in DuckDB over the baked facts (never the source DB),
    byte-identical (708/708 parity checks, *measured*). The single, priced runtime-compute exception.
- Slab semantics = authority #3/#4 territory: measure fixtures + slab fixtures pin C# ↔ TS.

---

## Section D — Front: render and fetch, same story (slides 14–17)

### Slide 14 — Problem: which tiles, this frame? And the frame after?
- Every camera move: descend the quadtree, keep tiles that intersect the viewport and are leaves or
  ≤512 px on screen — pure data-space math, identical for maps and charts.
- **Problem:** tiles load async — naive rendering shows holes, then popping. **Solution:** a
  missing tile is covered by its nearest loaded ancestor; the pyramid makes parent and children
  pixel-identical at swap size, so refinement is a single-frame, invisible event.
- **Problem:** pan/zoom latency you can feel. **Solution:** idle-time prefetch of parents (zoom-out),
  the neighbour ring (pan), children (zoom-in), capped; a service worker caches by `(version, tileKey)`.

### Slide 15 — Problem: decode jank
- Decode on the main thread and the map stutters exactly when the user is moving. **Solution:** a
  tile worker pool; format-2/3 buffers transfer (not copy) to the main thread as views.
- **War story:** the zoom stutter that taught us the rule — categorical columns crossing the worker
  boundary as ~1M JS strings per tile (structured clone). **Solution:** strings cross as integer
  codes + one small dictionary; identity strings decode lazily, one row, on click.

### Slide 16 — Problem: a filter or recolor that refetches anything has already lost
- Refetch/re-decode/re-upload on interaction puts the network back in the loop the bet removed.
- **Solution — the invariant:** `(version, tileKey)` is the only data identity. Filter, colour,
  measure are **GPU state**:
  - each filterable channel is one float slot (≤4) baked into the tile once; a filter change
    updates range uniforms — zero tile bytes touched;
  - value→colour is a small LUT texture sampled in the vertex shader; switching scale/theme/channel
    is a ~KB texture upload.
- **Problem:** a GPU colour path can drift from the legend. **Solution:** the LUT is baked from the
  same CPU scale function the legend renders from, parity-asserted per scale type without a GPU.

### Slide 17 — Problem: group-regime interaction ties it all together
- A filter in a coverage view: compile the context → fetch exactly the slab rows the fold reads
  (slide 13's directory) → fold on the worker (or remote, if priced) → write one folded column →
  the colour LUT does the rest (slide 16). No tile refetch, no geometry touched.
- This is the payoff slide: every structure from bake reappears at interaction time, doing the job
  it was shaped for.

---

## Section E — Authoring, status, wrap (slides 18–20)

### Slide 18 — Adding a visualization is a JSON file
- A view = viewport + mark + channel mapping + source query. Drop it in `views/`, bake, done.
  Show a real one (`views/ookla-fixed.json` or `mobile-coverage.json`), not a toy.
- Every map is fully described by its URL → an `<iframe>` reproduces it exactly.
  **Live demo:** change `&scale=`, watch it recolor with zero refetch — slide 16 happening live.

### Slide 19 — Honest status
- **Live end-to-end:** points + polygons on geo viewports; three reductions; full colour-scale
  system; GPU filters; inspect; embeds; slab companions with sliced fetch; priced remote fold;
  brotli wire path.
- **Opt-in / gated:** render pack (a packed version keeps no per-tile fallback — deliberate flag).
  All format choices ride the manifest; old bakes keep working.
- **Not built (specified):** geohash/H3 geometry, curated filter configs, more marks, client-side
  DuckDB full-fidelity viewport query at extreme scale.
- **Scale, honestly:** reference bakes ~7.6M facts / 627K marks; formats designed and *measured*
  against a 100M-fact scenario; "billions" is where the storage math points, not a benchmark run.

### Slide 20 — Takeaways
- **Every structure is a forced move.** Tiling ← shipping limits; exact edges ← seam ownership;
  canonical schema ← source leakage; format 2 ← decode copies; format 3 ← derivable geometry;
  in-archive compression ← Range vs Content-Encoding; slabs ← key bytes; cumulative planes ← range
  folds; sliced fetch ← selection-shaped reads; GPU state ← interaction cost.
- **Contracts are tested, not promised:** `Σ tiles == source`; four cross-language authorities
  pinned by shared fixtures.
- **`(version, tileKey)` is the only data identity.**
- Where to start reading: `docs/ARCHITECTURE.md`, then follow one view from `views/` through the
  bake to `App.tsx`.

---

## Backup slides
- Dev quickstart: docker ClickHouse → `Colossus.Bake -- <view-id>` → `Colossus.Server` → `npm run dev`.
- The verifier: `Colossus.Bake -- verify` (fidelity + companion witnesses through the pack).
- The four authorities and their fixtures: tiling · tile schema · geometry codec · measures/slab.
- Numbers table (*measured*, reference bakes): brotli ~5.5×; worst-tile interaction fetch
  50.5/25.3 → 7.8/7.5 MB; internal companions 713.8 → 385.4 MB gzipped; fold parity 708/708.
