# Colossus — Hard Rules (Invariants)

These are non-negotiable. If a change violates one of these, the change is wrong — stop and
reconsider, don't work around it. Every other document in docs/ is subordinate to this file.

---

## R1 — 100% visual fidelity. No aggregation, bucketing, or simplification of *rendered* marks.

Every mark the user sees is a **real source row/feature**. The engine never renders a bucketed,
binned, downsampled, or averaged stand-in *as if it were raw data*. "It looks about right" is a
bug, not a feature.

The one sanctioned exception: a view whose chart **is definitionally an aggregate** (histogram,
choropleth, heatmap grid, box plot). There the aggregate is the declared semantic — it is computed
in the source query (`GROUP BY`), nothing raw is hidden, and the descriptor says so explicitly.
This is not a fidelity violation; it is the chart.

## R2 — Full fidelity is the default path. Previews are the exception: labeled, and always resolvable.

Surviving 100M+ rows is a **hybrid** of two mechanisms, and the honesty rule (R1) governs both:

- **Default — full-fidelity viewport query.** The canonical store is Hilbert-sorted with row-group
  zone maps (R3/R4). The client (DuckDB-WASM) queries exactly the marks intersecting the viewport
  and renders **every one of them** — no sampling, no stand-ins. Within any feasible range this is
  the *only* path.
- **Exception — progressive preview at extreme zoom-out.** When the viewport intersects more real
  marks than can be shipped/drawn at once, the client may show a **fair random-prefix sample of real
  marks** as a **clearly-labeled preview** (e.g. "preview — sample of N of M; zoom in for full
  detail"). A preview is: (a) real rows only, never an aggregated or synthesized point; (b) fair
  ordering, so it is representative; (c) **always resolvable** — zooming or progressive streaming
  fills in until the viewport is at full fidelity. A preview that cannot be resolved to every mark
  is a bug.
- **The store is complete.** Whatever the delivery mechanism, the baked canonical store contains
  every source row exactly once (`Σ == source`, asserted by the fidelity test). Previews are drawn
  *from* it; they never replace it.
- **Reduction chooses delivery/membership, never the schema and never the mark.** The same canonical
  schema (R3) comes out of every primitive (RawPassthrough, QuadtreeLod, SignalM4, Aggregate). A
  primitive decides which real rows are delivered when; it does not alter columns, merge marks, or
  fabricate geometry.

## R3 — The tile format is canonical and source-independent.

Tiles are **Arrow IPC** (staging is Parquet), one canonical schema that does not vary by source type.
The source may be points (x/y), geo points (lon/lat), quadkeys, WKT/WKB
geometry, geohash, H3, or a database's native geometry — the **adapter normalizes all of them into
the canonical schema during bake**. Source shape must not leak past the extract stage. Serve, the
client, and the GPU never see a source-specific layout.

> **Why Arrow IPC, not Parquet, for tiles.** A tile is read once and handed straight to the GPU:
> `tableFromIPC` is essentially a memcpy, and the column buffers *are* the typed arrays deck.gl wants,
> so a tile flows disk → GPU with zero per-cell decode. Parquet remains the target for the *queryable*
> store (R4, S4) — the DuckDB-WASM predicate-pushdown path — but the render tiles are IPC. (The managed
> `Apache.Arrow` writer is also the only stable option: the DuckDB nanoarrow extension segfaults on
> DuckDB.NET 1.5.3.)

Canonical tile schema (target):

| Column         | Type                 | When            | Purpose                                                        |
|----------------|----------------------|-----------------|----------------------------------------------------------------|
| `x`            | Float32              | always          | representative point X (the point itself, or geometry centroid) |
| `y`            | Float32              | always          | representative point Y — drives sort, zone-maps, LOD, client query |
| `geometry`     | List\<Float32\>      | non-point marks | flat interleaved coords, deck.gl-ready; **absent for points**  |
| `part_offsets` | List\<Int32\>        | multi-part geom | ring/part start indices for polygons/paths                     |
| channels…      | Float32 / UInt8 / …  | per view        | `value`, `category`, and any named measures the view declares   |
| `id`           | dict\<String/UInt64\>| optional        | source identity for tooltips / client-side joins               |

Every feature has a representative `(x, y)` regardless of geometry — that is the unifying trick that
lets one set of spatial machinery (sort, zone-maps, quadtree, viewport query) work for points,
polygons, and lines identically.

## R4 — Tiles are optimized for two consumers at once: the GPU and client-side DuckDB.

- **GPU:** columns are zero-parse typed arrays fed straight to deck.gl **binary attributes**. No
  per-mark JavaScript objects, ever.
- **Client-side DuckDB (DuckDB-WASM):** the client queries tiles in-browser for filtering,
  crossfilter, and tooltips at hyperspeed. Tiles must be laid out to make that fast:
  **Hilbert-sorted by `(x, y)`** for spatial locality, **row-group zone maps** (min/max stats) for
  predicate pushdown / region pruning, **dictionary encoding** for low-cardinality channels,
  columnar throughout. Optimizing the tile layout for DuckDB predicate pushdown is a first-class
  concern, not an afterthought. This is the **S4 queryable-store path** (a Parquet sidecar); today's
  render tiles are Arrow IPC (R3) and the client filter path is not built yet.

## R5 — The source is the result of a query, behind a pluggable DB adapter.

Ingest is source-agnostic. A source is **arbitrary SQL** (the bake wraps it in a subquery), executed
through an `ISourceAdapter` (ClickHouse first; Postgres/MySQL/warehouse/files later). The adapter
owns all dialect specifics: the spatial sort expression, geometry decode (quadkey→polygon,
WKT→vertices, …), casts, bounds probe, and extract format. Adding a new source type or geometry type
is a new adapter/geometry case — **never** a new pipeline or a change to anything downstream of the
extract.

## R6 — Chart type is configuration, not a pipeline.

A **View** is a declarative descriptor: `viewport (geo | orthographic) + mark + channel mapping +
reduction primitive + source`. The engine has no "map mode" or "time-series mode" — one path draws
everything. Adding "candlestick over 200M ticks", "arc flow map", or a quadkey choropleth is a new
descriptor, not a new code path. Reduction is **dispatched** from `view.Reduction`, never hardcoded.

## R7 — Bake → immutable static files → serve → binary attributes → GPU.

The pipeline is `source (query) → bake → Arrow/Parquet tiles → static immutable serve → deck.gl
binary attributes → GPU`. Tiles are immutable and content-addressed by version. A bake writes a
fresh `<version>/` dir and **atomically flips `latest.json`** (temp + rename) — readers never see a
half-written version. On-prem only: **no cloud, no CDN spend.**

---

## Current conformance

Honest status so this file stays truthful as the slices land:

- **Met:**
  - R1 (leaves complete, fidelity test green), R7 (immutable serve + atomic `latest.json` flip).
  - R5: a source is a query behind `ISourceAdapter`; the ClickHouse adapter normalizes geometry +
    channels. R6: views are JSON config, reduction dispatched via a catalog (nothing hardcoded).
- **Partial:**
  - R2: the baked store is complete (quadtree leaves hold every row; the aggregate pyramid keeps
    every real cell and merges only sub-pixel ones) and coarse tiles are labeled in the client — but
    the baked pyramid is the *only* path today. The full-fidelity client-DuckDB viewport query and
    the progressive/streaming preview UX are not built (S4).
  - R3: tiles are **Arrow IPC** carrying `x, y` + every declared channel, and for area marks also
    `geometry` / `part_offsets` + a bake-time `triangles` column. Quadkey geometry ships (S3);
    WKT/geohash/H3 are still adapter TODOs.
  - R4: tiles are Hilbert-sorted, but the Parquet queryable-store sidecar, the DuckDB-WASM client, and
    deliberate zone-map / dictionary / bloom tuning are all pending (S4). Interactive `filters` are
    honored **client-side on the GPU**: each filterable channel is a `DataFilterExtension` float slot
    baked into the tile once (per-mark for points, per-vertex for area marks), and a filter change
    updates only `filterRange`/`filterEnabled` uniforms — zero tile bytes, no refetch/decode. The
    tile identity is `(version, tileKey)` alone; filter/color/measure are GPU state, never cache keys.
    Bake-side predicate pushdown into the queryable store is still pending (S4).
