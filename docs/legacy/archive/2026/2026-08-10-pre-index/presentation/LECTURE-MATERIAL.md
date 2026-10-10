# Colossus — Technical Lecture Material (source for Claude design)

Audience: technical team. ~60 min. This is a *teaching* deck: every core concept (feature,
partial aggregate, Arrow IPC, shader, texture, CSR, prefix sum…) is defined on the slide where it
first matters. The structure repeats one rhythm four times: **the problem → how existing tools
handle it → how we handle it, in detail.**

Terminology rule for the deck: **industry-standard terms only** — no project-internal names.
(Offline build, not "bake"; re-aggregation, not "fold"; feature, not "mark"; sidecar, not
"companion".)

Scale framing throughout: **raw source data in the hundreds of millions to billions of rows.**
Never anchor on a specific current dataset; improvements are quoted as measured ratios.

---

## Act I — The problem

### 1 — Title
- **Rendering billions of rows, honestly, in a browser.**
- Subtitle: a precompute-everything engine for full-fidelity interactive visualization.
- Visual: hero screenshot of a dense global view.

### 2 — The problem statement
- You have a database table with **billions of raw rows** — telemetry, measurements, events —
  each with a location (or an x/y), dimensions, and metrics.
- The ask: *see all of it* in a browser. Pan, zoom, filter, and have aggregates recompute live —
  without lying (no invented average dots, no "representative sample" pretending to be the data).
- The tension: a browser tab gets ~2–4 GB of memory, one main thread, and a user who expects
  every interaction under ~100 ms.

### 3 — Why this is genuinely hard (the four walls)
- **Network:** billions of rows × even 20 bytes = tens of GB. Can't ship it.
- **Memory:** even 1% of it doesn't fit a tab as JS objects (an object per row costs ~10× the data).
- **CPU:** the main thread renders the UI; any per-row work (parsing, triangulation, aggregation)
  at this scale means seconds of frozen page.
- **GPU:** it *can* draw tens of millions of primitives at 60 fps — but only if data arrives as
  raw binary buffers in exactly the layout the shaders read. Nothing upstream produces that
  naturally.
- Speaker note: the whole talk is "move every cost to a place where it's paid once, offline."

### 4 — How existing tools handle it
| Approach | Examples | Where it breaks |
|---|---|---|
| Aggregate first, render the aggregate | BI tools (Tableau, Power BI) | You see buckets, not data. Drill-down = new query = round trip. Fidelity is gone by design. |
| Server renders images | datashader, tile-image servers | Output is pixels: no per-feature identity, no client-side inspect/filter; every interaction re-renders server-side. |
| Cartographic vector tiles | Mapbox GL + tippecanoe, martin | Built for *maps*, not data: simplifies geometry and **drops features** at low zoom; no notion of aggregates recomputing under filters. |
| Ship it all to the client | deck.gl / kepler.gl on raw files | Superb renderer, but data must fit the tab; dies past a few million rows. |
| Query engine in the browser | DuckDB-WASM alone | Great at querying, but every pan/zoom/filter is a query over data it must first download; no render pipeline. |
| Live SQL per interaction | dashboard-on-warehouse | Latency + cost scale with users × interactions; the database becomes the frame budget. |
- Bottom line: every existing answer gives up one of **fidelity, interactivity, or scale**.
- Speaker note: we borrow from three of these (deck.gl for rendering, DuckDB for build-time and
  fallback compute, the tile pyramid idea from cartography) — the novelty is where the work happens.

### 5 — Our answer in one picture
- **`any database → offline build (once) → immutable static files → plain static serve →
  zero-copy decode → GPU`**
- Three commitments:
  1. **Full fidelity** — every rendered feature is a real source row. Aggregation only when the
     chart *is* an aggregate (histogram, choropleth), declared, never silent.
  2. **All expensive work happens offline, once** — extraction, spatial indexing, triangulation,
     partial aggregates, compression. Serving is dumb bytes; the client mostly memcpys.
  3. **Interaction touches state, not data** — filtering and recoloring change GPU uniforms and
     small fetches, never a re-query of the source database. The source DB is consulted exactly
     once, during the build.
- Diagram: the pipeline arrow with a cost bar under each stage — tall bars on the left (build),
  near-zero on the right (interaction).

### 6 — Vocabulary (the standard terms the rest of the talk uses precisely)
- **Feature** — one distinct geometry the user sees (a point, a polygon). One feature may be
  backed by many source rows.
- **Fact** — one source row belonging to a feature (data-warehouse sense: when geometry repeats,
  e.g. one cell measured per month per vendor).
- **Dimension / measure** — a categorical or temporal column you filter and group by / a numeric
  column you aggregate. (Standard BI meaning.)
- **Tile** — one file holding all features for one node of the spatial pyramid (`z/x/y`).
- **Partial aggregate** — an intermediate aggregation result (`sum`, `count`, `min`, `max`,
  weighted sums) that can be combined later without the raw values.
- **View config** — a JSON descriptor: source query + geometry + columns + aggregate definitions
  + color encoding. Adding a visualization is authoring config, not writing code.

---

## Act II — Getting bytes to pixels (the render path)

### 7 — Problem: file formats are hostile to GPUs
- What a GPU wants: contiguous typed arrays — `Float32Array` of positions, `Uint32Array` of
  triangle indices — handed over as-is.
- What data formats deliver: JSON/GeoJSON (parse every character, build objects), Parquet
  (excellent on disk, but decompress + decode per column), protobuf vector tiles (decode + still
  no triangles).
- How others handle it: parse/decode on the main thread or a worker, build per-feature objects,
  then *convert again* into buffers. At millions of features the conversion is the bottleneck,
  not the network.

### 8 — Our tile format: Arrow IPC, and what that buys
- **Apache Arrow** = a standardized *in-memory* columnar layout: each column is one contiguous
  typed buffer. **IPC** = that exact memory written to disk with a small framing header.
- Consequence: reading a tile is essentially a **memcpy** — no per-value decode. The column
  buffer in the file *is* the `Float32Array` the renderer needs.
- Why not Parquet for tiles? Parquet is encoded + compressed *per value block* — perfect for
  scan-heavy analytics, wrong for "read once, hand to GPU." (We still use Parquet where scanning
  is the job: build staging and the server-side aggregation fallback.)
- We harden it into a **zero-copy contract** per tile: a single record batch, no nulls in any
  column (sentinels instead), all numeric columns stored as `Float32` (the stored buffer is the
  render buffer), dictionary-encoded columns written in one canonical order (the codes in the
  file are the codes the client already knows — no remapping).
- Diagram: a tile file drawn as byte ranges, with arrows straight from column buffers to GPU
  attribute slots. Caption: "decode step: none."

### 9 — GPU 101, scoped to exactly what we use
- **Vertex** — one corner of a drawn primitive. A point feature = 1 vertex (expanded by the GPU);
  a polygon = its triangle corners.
- **Shader** — a small program the GPU runs in parallel: the **vertex shader** runs per vertex
  (computes position, color), the **fragment shader** per pixel.
- **Attribute** — per-vertex input data (position, per-feature value) — these are the typed
  arrays from slide 8.
- **Uniform** — one value shared by all vertices in a draw call (e.g. the active filter range).
- **Texture** — a small image the shader can read as a lookup table — not just for pictures.
- The rule that makes it fast: **per-feature data crosses to the GPU once; per-interaction data
  is uniforms and textures** (bytes: a handful, not megabytes).

### 10 — Polygons: triangulation, and why it happens offline
- GPUs draw triangles, not polygons. Turning a polygon (possibly with holes) into triangles =
  **tessellation** (e.g. the earcut algorithm) — nontrivial CPU work per ring, classically done
  in the browser at load time.
- How others handle it: tessellate on the client (main-thread stalls measured in seconds at
  scale) or pre-render to images (lose the feature).
- Ours: tessellate **during the offline build**, store the triangle index list as a tile column,
  with indices already tile-global (no per-feature rebasing on the client). The client uploads
  positions + indices; zero geometry math in the browser.
- Speaker note: this single decision was the largest render-path win in the project's history.

### 11 — Recoloring without touching data
- Problem: "color by X" where the scale or even the driving value changes at runtime. Naive
  answer: recompute an RGB array per feature and re-upload — megabytes per interaction.
- Ours: the color scale is sampled once into a tiny **RGBA lookup-table texture** (~256 texels).
  Each feature carries one float attribute (its scale value, uploaded once). The vertex shader
  maps value → texture coordinate → color.
- A recolor = replace a 1 KB texture + a few uniforms (domain, transform). Zero per-feature
  traffic.
- The CPU color-scale code stays the *authority*; the lookup table is asserted equal to it in
  tests — the GPU can't drift.

### 12 — Filtering without refetching
- Problem: toggling a filter usually means a new query or at least a re-scan of client data.
- Ours: every filterable column is stored in the tile as one float **attribute** per feature.
  A filter change updates two **uniforms** (`filterRange`, `filterEnabled`); the vertex shader
  discards non-matching features. Cost of a filter interaction: bytes, not megabytes; no fetch,
  no decode, no re-upload.
- Tiles stay resident on the GPU across interactions; the only cache identity is
  `(version, tileKey)` — color/filter state is GPU state, never part of data identity.
- Diagram: two panels — "typical: filter → query → transfer → parse → upload" vs
  "ours: filter → 2 uniforms."

---

## Act III — Getting billions of rows into tiles (the offline build)

### 13 — Problem: sources are heterogeneous; the pipeline must not be
- Real sources: any SQL database; geometry as lon/lat pairs, quadkeys, WKT/WKB, geohash, H3…
- How others handle it: per-source pipelines, per-geometry code paths, format leaks all the way
  to the client (every new source = new plumbing).
- Ours: the source is **an arbitrary SQL query behind a pluggable adapter**. The adapter owns all
  dialect + geometry specifics and normalizes everything into one canonical tile schema during
  extraction. Downstream (aggregation, tiles, client, GPU) never learns what kind of source
  existed.
- The unifying trick: every feature gets a representative `(x, y)` regardless of geometry type —
  so one set of spatial machinery (sorting, pyramid assignment, viewport math) serves points,
  polygons, and lines identically.

### 14 — The spatial pyramid
- Tiles form a quadtree pyramid over the data's bounding box: zoom z has 4^z tiles; each tile
  splits into 4 at z+1. The client fetches only tiles intersecting the viewport at the current
  zoom — **the working set is proportional to the screen, not the dataset.**
- The build *plans from the data*: it probes the source (row count, extent, density) and chooses
  strategy and pyramid depth. Nothing is authored per dataset — engine logic never names a
  dataset, column, or shape (a hard data-agnosticism rule).
- Coarse zoom levels hold a bounded, fairly-chosen subset of *real* rows (labeled as such; fully
  resolvable by zooming) — never synthesized stand-ins. The leaf level holds **every row exactly
  once**, which a verifier asserts (`Σ leaf rows == source rows`) on every build.
- Within tiles, rows are **Hilbert-sorted** (a space-filling curve: spatially close rows land
  close in the file) — locality for everything downstream.
- Diagram: pyramid with a viewport slicing through one level; verifier equation as a stamp.

### 15 — The build pipeline end to end
- `SQL query → adapter extraction → staging (Parquet) → per-level aggregation & partitioning
  (DuckDB) → tessellation → Arrow IPC tiles + manifest → atomic version flip`
- Everything is written to a fresh immutable `<version>/` directory; a tiny pointer file flips
  atomically — readers never see a half-written build; rollback = flip back.
- Serving is static file serving, full stop. No app server in the render path, no per-request
  compute, works from any dumb host.

---

## Act IV — Live aggregates over facts

### 16 — Problem: filter-dependent computed values
- The killer feature: geometry repeats — a feature's rows are its **facts** (e.g. one polygon
  measured per period per vendor). "Color by the dominant vendor *over the selected date range*"
  means: change the range → recompute an aggregate per feature → recolor. At full zoom-out that's
  **an aggregate over millions of facts, per interaction**.
- How others handle it:
  - BI tools: send a query per interaction (round trip, server load, seconds).
  - Pre-aggregated OLAP cubes: precompute every filter combination — combinatorial explosion,
    and ad-hoc ranges aren't in the cube.
  - Most rendering stacks: don't support it at all (color is static).

### 17 — Ours: re-aggregation from partial aggregates
- During the build we store, per (feature, dimension-combination), **partial aggregates**:
  `sum`, `count`, `min`, `max`, weighted sums. Partials are *associative*: any subset of facts
  can be combined later without the raw values. Lossless, exact — no sketches, no sampling.
- On a filter change, a worker recomputes each feature's aggregates: combine the partials of the
  facts that survive the filter, then finalize (`avg = sum/count`, `share = part/whole`,
  `argmax` picks the winning category). Typed arrays end to end — the main thread never
  aggregates.
- Aggregates are declared in config (`wavg(download, tests)`, `argmax(vendor, sum(tests))`) —
  the syntax *looks* like SQL but compiles to plans over partial aggregates; nothing is ever
  sent to a database at runtime.
- Filter semantics, one consistent rule: a filter on a per-feature column is a predicate
  (GPU-side, slide 12); a filter on a per-fact column re-scopes **every aggregate of every
  feature**.

### 18 — Problem inside the solution: the aggregate sidecar
- Those partial aggregates must live somewhere the client can fetch: a **sidecar** dataset
  beside each tile. Naively (one row per (feature, dimension values) with key columns): at
  billions of facts this is **gigabytes at rest and tens of MB per single-tile fetch** — and
  roughly *half of every row is repeated key material*.
- This is the classic trade nobody talks about: client-side interactivity means shipping the
  aggregation inputs. The rest of Act IV is how we made that affordable.

### 19 — Step 1: structure — from key columns to a cell space
- Every per-fact dimension becomes an **axis** with one of two algebras: **categorical**
  (filtered by equality; domain = its dictionary) or **ordered** (filtered by range; dates are
  just an instance). The cross product of axis domains is the tile's **cell space** — e.g.
  4 vendors × 8 periods = 32 cells.
- Now a fact's keys are *coordinates*: `cellId = categoryCode · T + orderedBin`. Key columns
  vanish into array indexing.
- The build **measures occupancy** (`facts / (features × cells)`) and picks the physical layout
  per view — recorded in the manifest; the client branches on the manifest, never sniffs data.
- Diagram: the cell grid, one feature's facts as filled cells, the cellId formula.

### 20 — Step 2: layout — dense planes or sparse CSR
- **Dense** (high occupancy): per partial aggregate, one **plane** — a `cells × features` 2-D
  array, cell-major. And the trick: along the ordered axis, subtractable aggregates are stored
  as **prefix sums** (running totals). A range query `[lo, hi]` becomes
  `cumulative[hi] − cumulative[lo−1]` — **two array reads per feature, O(1) in the range
  width**. (`min`/`max` can't subtract; they stay raw and are scanned.)
- **Sparse — CSR** (low occupancy; "compressed sparse row", the standard sparse-matrix layout):
  `offsets[features+1]` + `cellIds[nnz]` + one value array per aggregate. The filter compiles to
  a `Uint8[cells]` bitmask once, then one linear pass — no per-row key decode.
- Integer widths are chosen from measured counts (cell ids as u8/u16/u32); feature ids are
  eliminated entirely (they're the array index). Values stay f32/i32 — exact.
- The payoff beyond size: a dense plane's **cell row is contiguous bytes** — it is literally a
  GPU texture row, uploadable for a future GPU-side aggregation without reshaping.
- Diagram: row table morphing into stacked planes; one cell row highlighted; the prefix-sum
  subtraction drawn as two arrows into the plane.

### 21 — Step 3: packaging & compression — fetch only what the interaction needs
- **One archive file** per view version instead of hundreds of per-tile files. Each tile's bytes
  are an **independently gzip-compressed block**; a directory (`tileKey → byte offset, length`)
  rides the manifest. The client fetches with **HTTP Range** requests and decompresses with the
  browser-native `DecompressionStream` — zero JS decompression libraries.
- Why gzip: it's what `DecompressionStream` speaks everywhere. Why compress *inside* the
  archive: HTTP `Content-Encoding` doesn't compose with Range requests — a ranged slice of a
  compressed whole is garbage; a ranged slice of independently-compressed blocks is a valid
  block.
- **Per-plane ranges:** the directory also records byte ranges *per plane*, so an interaction
  fetches only the planes its active aggregates need — measured ~**3–6× smaller** worst-tile
  interaction fetch.
- **Cell-row slicing:** the cell ordering puts the ordered axis innermost, so a range query
  needs exactly two contiguous cell rows (`hi`, `lo−1`) per selected category — fetchable as
  tiny ranges; target a further ≥5× on top of plane splitting. Sparse layouts skip this (their
  blocks are small by construction) — the manifest records who supports what.
- Cache identity never changes: still `(version, tileKey)`; slices cache *under* their tile.
- Diagram: the archive as a byte strip; three zoom levels of granularity: whole tile → plane →
  cell row, each with its directory arrow. Caption: "a date-range drag fetches two rows, not a
  tile."

### 22 — Step 4: the escape hatch — server-side aggregation
- Some views will exceed any client budget (cell space × features too large even sliced). The
  build *prices* each view's sidecar; above budget, aggregation routes to a small server
  endpoint: the same declared aggregates, executed by DuckDB **over the build's Parquet
  artifacts** (never the source DB), returning finalized columns — ~4 bytes per feature per
  aggregate on the wire, instead of facts.
- Crucially it sits behind the *same client interface* (`aggregate(definitions, filters) →
  columns`) — the renderer cannot tell where the computation ran. Tiles remain immutable static
  files; this endpoint is the only compute in the serve path, and only for priced-over-budget
  views.
- Diagram: decision diamond — "sidecar under budget → client-side / over → server-side" — both
  arrows terminating in the same `aggregate()` interface box.

---

## Act V — Trust & close

### 23 — How we know it's correct
- The build is C#; the client is TypeScript. Every contract implemented twice is pinned by a
  **shared fixture file both test suites must pass**: tile math, tile schema, the aggregate
  grammar, and the sidecar byte layout. Change a contract → regenerate the fixture → both
  languages must agree, or CI fails. Drift is impossible to ship silently.
- Every physical re-encoding (rows → planes, files → archive) must produce **byte-identical
  aggregation results** on those fixtures. Formats changed repeatedly; semantics never did.
- Every build runs a verifier: **Σ count partials == source row count** — a single equation that
  catches dropped or duplicated facts end to end, read through the real archive + ranges.
- Byte-level spot checks: a ranged, gunzipped block is asserted equal to an independent decode.

### 24 — Recap: where every cost went
| Cost | Paid | How |
|---|---|---|
| Query the source | once, offline | adapter extraction |
| Spatial indexing, triangulation | offline | pyramid + precomputed triangle indices |
| Parse/decode on client | ~zero | Arrow IPC zero-copy contract |
| Filter interaction | ~zero bytes | GPU attributes + uniforms |
| Recolor interaction | ~1 KB | lookup-table texture |
| Aggregate recompute | small ranged fetch + worker pass | planes, prefix sums, plane/cell-row slicing |
| Over-budget views | one small response | server-side aggregation over build artifacts |
- Closing line: *fidelity, interactivity, and scale stop being a pick-two the moment you're
  willing to do all the work before anyone asks for it.*
- Q&A. Backup slides: plane/CSR byte layout, aggregate finalization semantics, archive directory
  format, measured benchmark tables.

---

## Diagram inventory for Claude design (payoff order)

1. **Pipeline with cost bars** (slide 5) — the thesis picture.
2. **Row table → stacked planes** (slide 20) — with cell-row highlight + prefix-sum arrows.
3. **Archive byte strip with 3 granularities** (slide 21) — tile → plane → cell row.
4. **"Filter = 2 uniforms" split panel** (slide 12).
5. **Cell space grid** (slide 19).
6. **Tile file → GPU attribute arrows** (slide 8).
7. **Aggregation routing diamond** (slide 22).
8. **Pyramid + viewport** (slide 14).

## Pre-talk checklist

- [ ] Fresh hero + demo screenshots (slides 1–2), and a recolor sequence for slides 16–17.
- [ ] Pull final measured ratios for slide 21 from the post-launch benchmark run and replace the
      "~3–6×" band with the exact numbers.
- [ ] Confirm server-side aggregation and cell-row slicing status match reality on talk day.
