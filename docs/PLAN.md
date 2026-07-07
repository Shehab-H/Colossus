# Colossus — Implementation Plan (Milestone 1: Walking Skeleton)

## Context

Colossus is an internal tool for visualizing very large datasets (10M–100M+ rows, variable per dataset) with **no aggregation, bucketing, or simplification** of raw marks — every point is real and eventually renderable at full fidelity. It is **not** a geospatial tool: geographic-points-on-a-map is one view; **any chart type over arbitrary (non-geo) data is an equal peer**. Constraints from design: **on-prem, no cloud, no CDN spend**, sub-second interactivity, and a **source-agnostic** ingest side (ClickHouse first; Postgres/MySQL/warehouse/files later).

The shared engine is one path regardless of what's being drawn: **bake → Arrow LOD tiles → static (immutable) serve → deck.gl binary attributes → GPU**, with zero per-mark JavaScript objects. Live streaming and multi-source adapters are later milestones layered on this skeleton.

## The core model — chart type is a config, not a pipeline

The engine never has a "time-series mode" or a "map mode." Every visualization decomposes onto two orthogonal axes:

**Axis 1 — Mark / geometry (how it's drawn).** A deck.gl layer choice plus a channel mapping (which columns → x, y, color, size, …): point, line, arc, rect/bar, polygon, heat, text. This axis is **feasibility-neutral** — it does not affect how we survive 100M rows.

**Axis 2 — Reduction primitive (what makes it feasible).** A **small closed set**, selected from the data + encoding, *not* from the chart's name:
- **Raw pass-through** — mark count under budget: ship all, no reduction.
- **Quadtree spatial LOD** — marks land on a 2-D continuous plane and overplot (maps by lon/lat, scatter/bubble by arbitrary x/y, arc endpoints). Random-prefix pyramid; sampling only where marks exceed pixels, only as fair ordering.
- **1-D signal downsampling (M4 / min-max)** — dense marks along one ordered axis mapped to pixel columns (line, area, candlestick). Per-pixel-column min/max/first/last → **pixel-lossless** at screen resolution.
- **Semantic aggregation / binning** — the chart *is* an aggregate (histogram, bar, heatmap grid, choropleth, box plot). Computed in DuckDB/ClickHouse; result is tiny. (Not a "no-bucketing" violation — the bar chart is definitionally the aggregate; nothing raw is hidden.)

A **View** is therefore a declarative descriptor: `viewport (geo | orthographic) + mark + channelMapping + reductionStrategy`. Adding "candlestick over 200M ticks" or "arc flow map" or "bubble scatter" is a new descriptor, not a new code path.

## M1 goal (proposed)

Prove the shared engine and the **hardest, highest-leverage reduction primitive (quadtree spatial LOD)** end-to-end, rendered through **two different viewports from the same tiling code** — a geographic map (MapLibre) and a non-geo orthographic scatter (arbitrary x/y) — driven purely by a View descriptor. This de-risks the "one engine, any chart" claim directly. Also stub the reduction-primitive **plugin interface** so M4 downsampling and aggregation slot in later without touching the engine. (Open item, non-blocking: whether to also ship a thin server-side aggregation example in M1 or defer to M2 — defaulting to defer.)

## Stack

- **Backend / bake / services: .NET (C#).** `DuckDB.NET` (out-of-core sort/partition/IO engine), `Apache.Arrow` (C# IPC read/write), `Parquet.NET` (staging). ClickHouse read via its **HTTP interface + `FORMAT Parquet`/`ArrowStream`** (`HttpClient` + Apache.Arrow); ClickHouse **`hilbertEncode`** does the spatial sort server-side. **ASP.NET Core (Kestrel)** serves tiles in dev (and later the API/WebSocket fan-out).
- **Frontend: React + TypeScript + Vite + deck.gl + MapLibre GL.** MapLibre (no token, no spend). `apache-arrow` (JS) parses tile bytes into typed arrays fed to deck.gl **binary attributes**. Geo uses a `GeoJsonLayer`/`ScatterplotLayer` under MapLibre; non-geo uses the same `ScatterplotLayer` under an `OrthographicView`.

## Repository layout (create under `C:\Users\pc\Desktop\Colossus`)

```
Colossus.sln
src/
  Colossus.Core/      # models + shared utils: ViewDescriptor, ReductionStrategy interface,
                      #   LOD/tile math, Hilbert util, Arrow helpers, Manifest
  Colossus.Seed/      # console: synthetic dataset generator -> ClickHouse (geo table + non-geo x/y table)
  Colossus.Bake/      # console: planner + extract + reduction (quadtree LOD builder) + manifest
  Colossus.Server/    # ASP.NET Core: static tile/manifest host (dev), later API/fan-out
web/                  # Vite + React + TS + deck.gl + MapLibre; View-descriptor-driven renderer
tiles/                # bake output (gitignored): latest.json + <version>/manifest.json + <version>/z/x/y.arrow
docker/               # docker-compose: ClickHouse (dev), nginx (prod parity)
docs/PLAN.md          # this plan, committed as the first step
README.md
.gitignore
```

## Milestone 1 steps

1. **Scaffold** solution, projects, `web/` (Vite React-TS), `docker/docker-compose.yml` (ClickHouse), `.gitignore`, commit **this plan to `docs/PLAN.md`**, `git init`.

2. **`Colossus.Core`** (reused by Bake + Server + shared with web via JSON contracts):
   - `ViewDescriptor` (viewport type, mark, channel mapping, reductionStrategy) and `ReductionStrategy` interface (the plugin seam; M1 implements `QuadtreeLod` + `RawPassthrough`, leaves `SignalM4` + `Aggregate` as declared-but-unimplemented).
   - `Manifest`, `TileId(z,x,y)`, `Bbox` (generic min/max over the view's two primary dims — lon/lat *or* x/y), `ColumnSchema`.
   - LOD/tile-coordinate math + tile-relative `float32`/`uint16` quantization; `HilbertIndex` util (fallback sort key for non-ClickHouse sources).
   - Arrow IPC read/write helpers (positions `float32`, value `float32`, category `uint8`).

3. **`Colossus.Seed`** — generate two synthetic datasets to exercise generality: (a) ~20M **geo** points (clustered metros) and (b) ~20M **non-geo** points with arbitrary numeric `x,y,value,category`; bulk-insert into ClickHouse.

4. **`Colossus.Bake`** — engine + quadtree primitive:
   - **Planner**: ClickHouse `count(*)` + `min/max` over the descriptor's two primary dims → regime + `tilePointBudget` (~250k–500k/leaf) + `maxZoom`.
   - **Extract**: ClickHouse HTTP `SELECT <dims...> ORDER BY hilbertEncode(<dims>) FORMAT Parquet` → staging Parquet (pre-sorted). Works identically for (lon,lat) and (x,y).
   - **Reduce (QuadtreeLod)**: `DuckDB.NET` runs **adaptive split-on-overflow** partitioning (subdivide only when a node exceeds budget; empty regions get no tiles), writing each leaf as an Arrow tile plus **random-prefix subsets** for ancestor levels.
   - **Manifest**: write `<version>/manifest.json` (version, view descriptor, regime, bbox, minZoom, maxZoom, per-tile counts), then **atomically flip `latest.json`** (temp + rename).

5. **`Colossus.Server`** (dev) — ASP.NET Core static host for `/tiles/**` with `Cache-Control: immutable`, `latest.json`/`manifest.json` at `max-age=60`, CORS for Vite. Prod swaps to nginx, identical headers.

6. **`web/`** — Vite React-TS, **View-descriptor-driven**:
   - Read `latest.json`→`manifest.json`+descriptor. Choose viewport: `MapLibre + deck.gl` for geo, `OrthographicView` for non-geo — **same tile loader either way**.
   - On viewport change compute intersecting tiles at needed depth; `fetch` `.arrow`; parse with `apache-arrow` into typed arrays; render `ScatterplotLayer` via **binary attributes**; color/size ramp from `value`. LRU-evict off-screen tiles.

7. **Wire end-to-end**; confirm the **geo dataset renders on the map** and the **non-geo dataset renders as an orthographic scatter** through the same engine, both panning/zooming with bounded memory.

## Roadmap (later milestones — not in M1)

- **M2** — Remaining reduction primitives (`SignalM4` 1-D downsampling; `Aggregate`/binning via DuckDB/ClickHouse) + more marks (line, bar, arc, polygon, heat) + GPU `DataFilterExtension` crossfilter. This is where "all chart types" fills in.
- **M3** — Variable-length regimes (ship-whole / huge-LOD) + planner auto-select + DuckDB-WASM chart-aggregate sidecar.
- **M4** — Live streaming: broker (Redpanda/NATS) → ASP.NET Core WebSocket fan-out (200ms micro-batched Arrow frames) → ring-buffer hot layer; ClickHouse sink; reconnect backfill.
- **M5** — Multi-source adapters behind the "snapshot + change-stream" contract (Postgres WAL, MySQL binlog, files); DuckDB as reference reader.
- **M6** — Prod hardening: nginx swap, auth (signed cookies/SSO) if private, incremental per-tile bakes, OPFS client cache.

## Verification (end-to-end for M1)

1. `docker compose up` ClickHouse; run `Colossus.Seed` (both datasets); confirm row counts.
2. Run `Colossus.Bake` for each View descriptor; confirm `tiles/<version>/manifest.json` + `z/x/y.arrow`; spot-check tile sizes (few-hundred-KB band) and per-tile counts respect budget.
3. **Fidelity smoke test** (automated): bake a ~100k dataset and assert **every input row appears in exactly one leaf tile** (Σ leaf counts == input count; no drops, no dupes) — guards the no-simplification invariant. Same test runs for both geo and non-geo, proving the primitive is dimension-agnostic.
4. Run `Colossus.Server` + Vite; open both views: geo points on the map **and** non-geo scatter in the orthographic viewport render from the same tiles code; panning issues `.arrow` GETs; repeat load hits browser cache; tab memory stays bounded (~hundreds of MB).
