# Colossus — Technical Introduction (Deck Outline)

**Audience:** mixed technical team (engineers + PM/design)
**Length:** ~18 slides, deep-dive
**Goal:** everyone leaves understanding what Colossus does, why it's hard, and the core ideas that make it work.

---

## Section A — The Problem & The Promise (slides 1–4)

### Slide 1 — Title
- **Colossus**
- "Render 10M–100M+ raw data points, on a map or any chart, with **zero aggregation** — over a single engine."
- Presenter name / date.

### Slide 2 — The problem in one picture
- A map with 100 million points. You *cannot*:
  - hold them all in browser memory,
  - draw them all naively on the GPU,
  - ship them all over the wire at once.
- The industry's usual answer: **aggregate** — bin, cluster, downsample, average.
- Our answer: **don't**.

### Slide 3 — The honesty principle (RULES R1)
- Every mark the user sees is a **real source row** — never a bucketed or averaged stand-in.
- > "It looks about right" is a bug, not a feature.
- The *one* sanctioned exception: charts that are **definitionally** aggregates (histogram, choropleth, heatmap) — declared explicitly, computed in the query, nothing raw hidden.

### Slide 4 — Full fidelity by default, previews as the exception (R2)
- **Default:** query exactly the marks in the viewport, render every one.
- **Exception (extreme zoom-out):** a *fair, labeled* random-prefix sample of **real** rows — "preview: sample of N of M, zoom in for detail."
- A preview is always **resolvable** — zoom/stream fills it in to 100%. A preview that can't resolve is a bug.
- The baked store is always **complete**: `Σ tiles == source` (asserted by a fidelity test).

---

## Section B — The Pipeline (slides 5–7)

### Slide 5 — The spine (R7)
- `source query (ClickHouse) → bake → Arrow IPC LOD tiles → static immutable serve → deck.gl binary attributes → GPU`
- Walk each arrow left to right. This one line *is* the system.

### Slide 6 — Bake once, serve forever
- Baking is the heavy lifting done **offline**: reduce, sort, tile, write.
- Output = **immutable, content-addressed** version directory.
- Atomic publish: write `<version>/`, then rename `latest.json` — readers never see a half-written version.
- On-prem only: **no cloud, no CDN spend.**

### Slide 7 — Reduction is a *primitive*, chosen by data
- The bake planner probes the source and picks a reduction: RawPassthrough, QuadtreeLod, Aggregate (SignalM4 next).
- Every primitive emits the **same canonical schema** — it decides *which real rows ship when*, never *what a mark is*.
- Reduction is dispatched from config, never hardcoded.

---

## Section C — Architecture (slides 8–10)

### Slide 8 — Clean architecture, dependencies point inward
- `Domain` (models + ports, zero I/O) → `Application` (use cases) → `Infrastructure` (adapters) → `Hosts` (Bake / Server, thin).
- Domain knows nothing of ClickHouse, DuckDB, ASP.NET, or the filesystem.
- One composition root (`AddColossus`) wires the whole graph; both hosts call it.

### Slide 9 — Plugin seams: add a class, not a pipeline
- New source geometry → an `IGeometrySql` class (point/quadkey today; WKT/geohash/H3 = new classes).
- New reduction → an `IReductionStrategy`.
- New database → an `ISourceAdapter` (ClickHouse today; Postgres/warehouse/files later).
- Growth = a small class at a seam, never editing a pipeline.

### Slide 10 — The two authorities (how we don't silently drift)
- Two contracts live in **multiple languages** (C# + TypeScript):
  1. **The tiling scheme** — which tile a point falls in.
  2. **The canonical tile schema** — the column names every tile carries.
- Each has **one source of truth** + a **shared fixture** that pins every copy. Change the scheme → regenerate the fixture → both sides must pass.

---

## Section D — The Tile Format (the clever core) (slides 11–13)

### Slide 11 — One canonical, source-independent schema (R3)
- Points, geo-points, quadkeys, WKT, geohash, H3 — the **adapter normalizes all of them** into one schema at bake.
- Source shape never leaks past extract. Serve, client, and GPU never see a source-specific layout.
- The unifying trick: **every feature has a representative `(x, y)`** — so one set of spatial machinery (sort, zone-maps, quadtree, viewport query) works for points, polygons, and lines identically.

### Slide 12 — Why Arrow IPC, not Parquet, for render tiles
- A tile is read **once** and handed straight to the GPU.
- `tableFromIPC` is basically a **memcpy** — the column buffers *are* the typed arrays deck.gl wants.
- Disk → GPU with **zero per-cell decode**.
- Parquet stays the target for the *queryable* store (DuckDB predicate pushdown); render tiles are IPC.

### Slide 13 — Zero-copy, evolving (tile formats 1 → 2 → 3)
- **Format 2:** one record batch, no nulls, tile-global triangles, canonical dict order, f32 measures → client decodes as **typed-array views over the one fetched buffer** (no column copies).
- **Format 3 (area marks):** geometry is ~69–99% of the bytes and mechanically derivable → drop it, ship one self-describing binary payload, decode back **bit-for-bit** in a worker.
- Older formats stay readable forever; the manifest gates.

---

## Section E — GPU Residency & Config (slides 14–16)

### Slide 14 — Filter / color / measure are GPU *state*, not fetches
- The core invariant: **`(version, tileKey)` is the only data identity.**
- A **filter** change = update `DataFilterExtension` uniforms → **zero tile bytes touched**.
- A **recolor** = a ~4KB LUT texture upload → **no per-mark data moves**.
- This is what makes interaction feel instant at 100M marks.

### Slide 15 — "Chart type is configuration, not a pipeline" (R6)
- A **view** = `viewport + mark + channel mapping + reduction + source` — a declarative JSON file. No code, no redeploy.
- One render path draws a map, a scatter plot, a choropleth, a candlestick.
- Show the minimal `geo-points` view JSON as a concrete example.

### Slide 16 — URL-addressable, embeddable maps
- Every map is fully described by its URL → an `<iframe>` reproduces it exactly.
- `embed=1` for a chromeless frame; pin view, color, scale, bins, theme, camera, filters via query params.
- Live demo idea: paste a URL, tweak `&scale=` / `&theme=`, watch it change.

---

## Section F — The Frontier & Wrap (slides 17–18)

### Slide 17 — Where the work is now: companion-scale
- The next hard problem: group/measure views write a **fact companion** per tile; at 100M facts that's ~3 GB and a single dense tile can be tens of MB.
- The initiative: **slab format** (indexed planes, not key columns), **remote fold routing**, **context-sliced fetch** (transfer proportional to the *selection*, not the whole cell space).
- Several requirements already **built + verified**; lossless only, semantics pinned by cross-language fixtures.

### Slide 18 — Takeaways
- **Fidelity is the product** — no fake marks, ever.
- **Bake hard, serve dumb** — immutable static files, no runtime compute on the render path.
- **One schema, one render path** — geometry, source, and chart type are all normalized away.
- **Interaction is GPU state** — `(version, tileKey)` is the only identity.
- Q&A.

---

## Optional backup slides
- The 7 RULES on one slide (reference card).
- Testing strategy: `dotnet test`, Vitest, cross-language conformance, `verify` fidelity invariant.
- Repo layout table (from the README) for new contributors.
