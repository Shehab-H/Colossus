# Colossus — Design & Roadmap

Colossus renders very large datasets (10M–100M+ rows) with **no aggregation or simplification of raw
marks** — on a map or in any chart — through one engine. It is a **visualization product**, not a fixed
map: geographic points are one view; any mark over arbitrary data is an equal peer.

The invariants are in [RULES.md](RULES.md) (the authority); the code layout and the two internal
authorities are in [ARCHITECTURE.md](ARCHITECTURE.md); the authored config surface is in
[VIEW_CONFIG.md](VIEW_CONFIG.md). This file is the design in brief and the forward roadmap.

## The core model — a chart is a config, not a pipeline

Every visualization decomposes onto two orthogonal axes, so the engine never has a "map mode":

- **Mark / geometry** — how it's drawn: a deck.gl layer + a channel mapping (which columns → x, y,
  color, size). Point, polygon today; line, arc, rect, heat, text are new descriptors, not new code.
- **Reduction primitive** — what makes it feasible, chosen from the data's shape (not the chart's
  name) by the [bake planner](../src/Colossus.Application/BakePlanner.cs): `rawPassthrough` (under
  budget), `quadtreeLod` (overplotting points), `aggregate` (pixel pyramid for area marks); `signalM4`
  (1-D min/max) is the next primitive.

A **view** is therefore a declarative descriptor — `viewport + mark + channel mapping + source` — and
adding "candlestick over 200M ticks" or an arc flow map is authoring a file, not writing a pipeline.

## The pipeline

```
source query (ClickHouse) → bake (DuckDB, out-of-core) → Arrow IPC LOD tiles
  → static immutable serve → deck.gl binary attributes → GPU
```

Zero per-mark JavaScript objects reach the render loop: a tile is `tableFromIPC` (a memcpy) and its
column buffers *are* the typed arrays deck.gl wants. Polygons are **tessellated at bake time** — the
client hands deck external triangle indices and never runs earcut on the main thread. Tiles are
immutable, content-addressed by version; a bake writes a fresh `<version>/` and atomically flips
`latest.json`. On-prem only — no cloud, no CDN spend.

## Status — what works today

- **Engine & serve:** planner-chosen reduction (`rawPassthrough` / `quadtreeLod` / `aggregate`),
  Arrow IPC tiles, immutable static serve + atomic `latest.json`, cross-language tiling conformance
  (C# ↔ SQL ↔ TS pinned by a shared fixture), the fidelity invariant test (Σ leaves == source).
- **Source:** a source is arbitrary SQL behind `ISourceAdapter` (ClickHouse); geometry `xy`, `lonLat`,
  `quadkey`; typed channels (measure / dimension / temporal / identity); coarse `bakeFilters`.
- **Client:** geo (MapLibre) viewport with an orthographic path in place; `point` + `polygon` marks;
  a framework-free tile cache via `useSyncExternalStore`; the view registry API (list / get / upload).
- **Color:** a full scale subsystem — `linear` / `log` / `sqrt` / `diverging` / `quantize` / `quantile`
  / `threshold` / `ordinal` / `categorical`, across any datatype, with named sequential / diverging /
  categorical schemes (colorblind-safe default) and an explicit-palette closed set. Every color-by
  map shows a **legend** derived from the same scale.
- **Interaction:** click-to-inspect panel (`inspect` config, nullable).

## Roadmap — the next cycles

Ordered roughly by leverage; each is an addition at an existing seam, not a rewrite.

1. **Interactive filtering.** `filters[]` (select / multiSelect / dateRange / range) over carried
   channels, live with no re-bake — the client query path (GPU `DataFilterExtension` and/or an
   off-hot-path in-browser query). Declared in config today, not yet honored.
2. **Queryable store + true full-fidelity at any scale.** The Parquet sidecar (Hilbert sort, row-group
   zone maps, dictionary/bloom) + a viewport query so the default path renders *every* mark
   intersecting the viewport, with a labeled, resolvable **preview** only at extreme zoom-out (RULES
   R2/R4). This is the `storage` config and the biggest remaining piece.
3. **More marks & encodings.** `line` / `arc` / `rect` / `heat` / `text`, `size` encoding, and the
   `signalM4` 1-D downsampling primitive (line/area/candlestick, pixel-lossless).
4. **More geometry & sources.** `wkt` / `geohash` / `h3` geometry; Postgres / MySQL / file adapters
   behind the same query-plus-role contract.
5. **Non-geo views in practice** — orthographic scatter/bubble configs exercising the same tiling code.
6. **Live streaming & prod hardening** (later) — broker → WebSocket fan-out → ring-buffer hot layer;
   nginx swap, auth, incremental per-tile bakes, OPFS client cache.

## Verification

`dotnet test` covers the pure bake logic + the cross-language tiling conformance; `web/` runs `tsc`,
`oxlint`, and Vitest (scale engine + tiling mirror). `dotnet run --project src/Colossus.Bake -- verify`
asserts the fidelity invariant against baked tiles; the app renders through the preview harness.
