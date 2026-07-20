# Colossus — Technical Introduction (Deck Outline, v3)

**Audience:** mixed technical team (engineers + PM/design)
**Length:** ~18 slides
**Tone:** down to earth. Real numbers where we have them (marked *measured* when they come from our
reference bakes), honest about what's live vs. opt-in vs. not built. Code references are footnotes for
the curious, not slide headlines.

---

## Section A — The Problem (slides 1–4)

### Slide 1 — The problem
- You have tens of millions to billions of rows — speed tests, coverage cells, buildings, events —
  and people who need to *see* them and *interact* with them: pan, zoom, filter, click a mark, ask "why
  is this one red?"
- Every mark on screen should be a real row. Filtering should feel instant. Serving it shouldn't
  require a compute cluster per viewer.
- Those three wants pull against each other. This talk is about how we resolve the tension.

### Slide 2 — Why the sizes are brutal (back-of-envelope)
- 100M marks × ~30–40 bytes each (position + a few channels) ≈ **3–4 GB** — before geometry.
  Polygon geometry multiplies that; in our tiles it's 69–99% of a polygon tile's bytes.
- A browser tab will not hold that. A network will not ship it per page load. And even if it could,
  a naive draw of 100M anything is not interactive.
- So *something* must give: either what you show, when you ship it, or how much work each
  interaction costs. The whole design is about choosing carefully what gives.

### Slide 3 — What existing tools give up (and why that's fair, but not enough)
Each of these is good at its job. None gives "every real mark, interactive, cheap to serve":
- **Live BI charts (Superset/Tableau/Grafana-style):** every pan/filter is a `GROUP BY` round-trip
  to the database. You see aggregates, not marks; latency is a query; cost scales with viewers.
- **Vector map tiles (Mapbox-style):** built for cartography — they *simplify and drop* features
  per zoom to hit tile budgets. Great for roads; wrong when each feature is a data point someone
  will filter and inspect.
- **Server-rendered rasters (datashader-style):** the server draws an image of the data. Faithful
  density, but marks are gone — no client-side filter, no click-to-inspect, and every interaction
  is a server render.
- **Sampling:** fast and simple, but unlabeled samples quietly lie, and most tools' samples can't
  be resolved to the full data by zooming.

### Slide 4 — The bet Colossus makes (and what it costs)
- **Do the heavy work once, at bake.** Reduce, sort, tile, precompress — batch cost, paid per
  dataset version, not per viewer or per interaction.
- **Serve static bytes.** After a bake there is no compute on the render path — a static file
  server (with HTTP Range support) is the whole backend. One deliberate exception exists for
  oversized fold workloads (slide 15).
- **Make interaction GPU state.** Filters and recolors change uniforms and small textures, not data.
- **The honest costs:** data is a snapshot (re-bake to update — this is not a live-query system);
  bakes take real time (compression alone is minutes of CPU, parallelized); storage is spent
  deliberately to save wire and compute later.

---

## Section B — From query to pyramid (slides 5–7)

### Slide 5 — Ingest: a source is a query
- A view names an adapter and arbitrary SQL. The engine probes it — bounding box, row count,
  **distinct geometries** — then extracts once into a spatially sorted Parquet staging table.
- Geometry is normalized at extract: lon/lat and x/y points, quadkeys (→ cell polygons), WKT today;
  each kind is one small SQL-generating class, so a new geometry type touches nothing downstream.
- After extract, nothing in the system knows or cares what database the data came from.
  *(Code: `ISourceAdapter`, `GeometrySqlFactory`.)*

### Slide 6 — Planning: the data's shape picks the strategy
- Nobody authors "this is a heatmap pipeline." The planner reads the probe:
```csharp
if (probe.Count <= _tilePointBudget)          // fits in one tile
    return ReductionKind.RawPassthrough;
if (areaMark || rowsPerShape >= 4.0)          // area marks, or a fact cube over few shapes
    return ReductionKind.Aggregate;
return ReductionKind.QuadtreeLod;             // a genuine point cloud
```
- Concrete budgets: **250k marks per leaf tile**, a **512×512 grid** per tile (≈1 grid cell per
  screen pixel at selection size), depth derived from distinct-shape count.
  *(Code: `BakePlanner`.)*

### Slide 7 — The pyramid keeps every row, honestly
- **Leaves are complete**: every source row lands in exactly one leaf, once. A verifier asserts
  `Σ tiles == source` against every bake — fidelity is a test, not a hope.
- Coarse levels are honest about what they are: the aggregate pyramid's cells are true means of
  children; the point pyramid's coarse tiles are a labeled preview that resolves as you zoom.
- Tile membership uses exact edge math — a power-of-two grid where a tile's max edge is bit-for-bit
  its neighbour's min edge, so a point on a seam lands in exactly one tile. The same math exists in
  C#, in generated DuckDB SQL, and in TypeScript, pinned together by one shared test fixture.

---

## Section C — What a baked version actually is (slides 8–11)

### Slide 8 — A version directory on disk (the real contents)
Not "a folder of Arrow files" — a layered storage design, all gated by one manifest:
```
<version>/
  manifest.json          # gates every format choice below; client branches on this, never guesses
  z/x/y.arrow            # render tiles — Arrow IPC, format 2 or 3
  z/x/y.arrow.br         # brotli siblings for the wire (~5× smaller, measured)
  render.pack + .dict    # (opt-in) per-column archive replacing the per-tile files
  facts.pack + .dict     # (group regime) the measures' fact companions, packed
  facts.parquet          # (group regime) retained facts for the server-side fold
latest.json              # atomic flip — readers never see a half-written version
```
- Immutable and content-addressed: a bake writes a fresh version, then renames `latest.json`.

### Slide 9 — Render tiles: format matters down to the byte
- **Format 2 — zero-copy contract:** one record batch, no nulls, dictionary columns in canonical
  order, f32 measures, triangle indices rebased at bake. The client reads columns as typed-array
  views over the one fetched buffer — no per-cell decode, no tessellation, no remap.
- **Format 3 — geometry encoded:** polygon geometry + triangles are 69–99% of tile bytes and
  mechanically derivable, so the bake replaces them with one binary payload; a worker decodes it
  back to the exact format-2 buffers, bit-for-bit (two codecs, chosen per tile from the data:
  rectangle corner-tables for grid cells, delta-zigzag coordinate streams for real rings).
- **On the wire:** every tile gets a brotli sibling at max settings (quality 11, 16 MB window —
  ~5.5× on large tiles, *measured*; ~23 s for the worst tile, a batch cost). Served via
  `Content-Encoding: br`, so the browser's network stack decodes and the client code sees identical
  bytes. Older formats stay readable forever; the manifest gates.

### Slide 10 — The render pack: don't ship columns nobody is reading
- The lossless floor: real-valued f32 measure planes barely compress. The remaining win is **not
  sending them until an interaction reads them**.
- So (opt-in, per bake) tiles are split into per-column blocks inside one archive, each block
  independently zstd-compressed with a **trained shared dictionary** (small blocks compress poorly
  alone). Block order is deliberate: geometry → default colour channel → filter slots → the rest —
  a first paint is one contiguous HTTP Range read.
- Why compression lives *inside* archives: `Content-Encoding` doesn't compose with Range requests.
  This rule shapes both packs.
  *(Code: `RenderPackWriter`; client `fetchPackBlocks`.)*

### Slide 11 — Companions: the second dataset hiding inside the first
- Group-regime views (e.g. coverage: many facts per cell across operator × quarter) need per-mark
  **measures** recomputed under the active filters. The raw ingredients — partial aggregates per
  `(mark, grain cell)` — are their own dataset, and at scale it dwarfs the render tiles.
- **v1, row form:** one Arrow file of key columns + partials per tile. Honest but ~28–36 B/fact;
  a dense tile could be tens of MB, and half of every row was key material.
- **v2, slabs:** keys vanish into array indexing — per tile, one plane per partial over
  `cells × marks`. Dense tiles store cumulative (prefix-summed) planes, so a date-range fold is
  two slice subtractions; sparse tiles store CSR. The choice is **measured per tile from occupancy**,
  recorded in the manifest — never authored.
- **Fetch is selection-shaped:** a per-plane + per-cell-row directory means a filter interaction
  fetches only the rows the fold will read. *Measured on our worst reference tile: 50.5 MB →
  7.8 MB per interaction from plane splitting, ≥5× further from cell-run slicing.*

---

## Section D — The client (slides 12–15)

### Slide 12 — Choosing what to draw is pure math
- Every camera frame: descend the quadtree, keep tiles intersecting the viewport that are leaves or
  ≤512 px on screen. While a tile loads, its nearest loaded ancestor covers it — parent and children
  are pixel-identical at swap size, so refinement is a single-frame, invisible event.
- Idle time prefetches parents (zoom-out), the neighbor ring (pan), and children (zoom-in), capped
  so a burst can't flood the loader. A service worker caches tiles by `(version, tileKey)`.

### Slide 13 — Decode without copying
- Tiles decode on a worker pool. Format-2 columns become views over the single fetched buffer —
  network → worker → GPU, transferred, never copied.
- The one lesson worth telling as a war story: row-wise JS strings crossing the worker boundary
  (cloning ~1M strings per tile) *was* the zoom stutter. Now categorical columns cross as integer
  codes + a small dictionary; identity strings decode lazily, one row, on click.

### Slide 14 — Interaction is GPU state
- The invariant: **`(version, tileKey)` is the only data identity. Filter, color, and measure are
  GPU state — never a reason to fetch, decode, or re-upload.**
- Filters: each filterable channel is one float slot (up to 4) baked into the tile once; a filter
  change updates `filterRange` uniforms. Zero tile bytes touched.
- Color: value→color lives in a small lookup-table texture sampled in the vertex shader; the LUT is
  baked from the same CPU scale the legend uses, so the GPU cannot disagree with the legend.
  Switching scale/theme/channel is a ~KB texture upload.

### Slide 15 — Folding measures (and the one server exception)
- Filter a coverage view and every visible cell's measures recompute over the surviving facts —
  a fold over companion partials, on the worker, vectorized over slab planes.
- If a view's slab exceeds a **32 MB** client budget, the planner prices the fold as remote: one
  endpoint runs the same fold in DuckDB **over the baked facts parquet — never the source DB** —
  and ships folded columns (marks × measures × 4 B). Verified byte-identical to the client fold
  (708/708 checks on reference views); today's reference views price client-side, so the remote
  path is exercised by a force flag.
- This is the engine's single runtime-compute component, added deliberately and priced, not defaulted.

---

## Section E — Authoring, status, wrap (slides 16–18)

### Slide 16 — Adding a visualization is a JSON file
- A view = viewport + mark + channel mapping + source query. Drop it in `views/`, bake, done —
  no code, no redeploy. Show a real one from the repo (`views/ookla-fixed.json` or
  `mobile-coverage.json`) rather than a toy.
- Every map is fully described by its URL — view, color channel, scale, theme, camera, filters —
  so an `<iframe>` reproduces it exactly. **Live demo:** change `&scale=`, watch it recolor with
  zero refetch (that's slide 14 happening).

### Slide 17 — Honest status
- **Live end-to-end:** points + polygons on geo viewports; three reductions; the full color-scale
  system; GPU filters; click-to-inspect; embeds; group-regime measures with slab companions,
  packed + sliced fetch; remote fold behind a priced route; brotli tile wire path.
- **Opt-in / gated:** render pack (bake flag; a packed version keeps no per-tile fallback, so it's
  deliberate). Per-view choices all ride the manifest — old bakes keep working; nothing breaks on
  format evolution.
- **Not built yet (specified):** geohash/H3 geometry, curated filter configs, more marks, the
  client-side DuckDB query path for extreme-scale full-fidelity viewport queries.
- **Scale, honestly:** reference bakes today are ~7.6M facts / 627K marks (coverage) with the
  format designed and measured against a 100M-fact scenario. "Billions" is the direction the
  storage math points, not a benchmark we've run — say exactly that if asked.

### Slide 18 — Takeaways
- **Bake hard, serve static** — per-viewer cost is a file read; the exception (remote fold) is
  priced, not defaulted.
- **Fidelity is tested, not promised** — `Σ tiles == source`, cross-language fixtures pin every
  format contract (tiling, schema, geometry codec, measures, slabs).
- **Bytes are designed, not emitted** — formats 2/3, brotli siblings, dictionary-trained packs,
  cumulative slab planes, selection-shaped fetch. Storage structure *is* the performance story.
- **Interaction is GPU state** — `(version, tileKey)` is the only data identity.
- Where to start reading: `docs/ARCHITECTURE.md`, then follow one view from `views/` through
  `BakeViewUseCase` to `App.tsx`.

---

## Backup slides
- Dev quickstart: docker ClickHouse → `Colossus.Bake -- <view-id>` → `Colossus.Server` → `npm run dev`.
- The verifier: `Colossus.Bake -- verify` (fidelity + companion witnesses through the pack).
- The four cross-language authorities and their fixtures (tiling, tile schema, geometry codec, measures/slab).
- Numbers table (all *measured*, reference bakes): brotli ~5.5×; worst-tile interaction fetch
  50.5/25.3 → 7.8/7.5 MB; internal companions 713.8 → 385.4 MB gzipped; remote fold parity 708/708.
