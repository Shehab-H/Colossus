# Colossus — View Configuration Reference

A **view** is a declarative JSON document. It is the *only* thing you author to add a visualization —
no code, no redeploy. Drop a file in `views/<id>.json` (or upload it via `POST /api/views`), bake it,
and it's live. This file documents every field.

The rules a view may never violate are in [RULES.md](RULES.md); the pipeline is in [PLAN.md](PLAN.md).

> **Support status.** Fields are tagged **[S1]** (works today), **[S2]** (canonical multi-channel
> tiles), **[S3]** (quadkey/WKT geometry + polygon mark), **[S4]** (Parquet store + DuckDB-WASM +
> filters). Authoring an unsupported field is not an error — the bake ignores what it can't yet honor
> and says so. Right now **only the 2-D map (`viewport: geo`) is in scope.**

---

## Minimal example — points on a map (runnable in S1)

```json
{
  "id": "geo-points",
  "title": "Synthetic metro points",
  "viewport": "geo",
  "mark": "point",
  "reduction": "quadtreeLod",
  "source": {
    "adapter": "clickhouse",
    "query": "SELECT lon, lat, value, category FROM colossus.points_geo",
    "geometry": { "kind": "lonLat", "lon": "lon", "lat": "lat" },
    "channels": [
      { "name": "value",    "column": "value",    "role": "measure",   "type": "f32" },
      { "name": "category", "column": "category", "role": "dimension", "type": "u8" }
    ]
  }
}
```

## Target example — quadkey choropleth over the 300M cube (S3/S4)

```json
{
  "id": "crowd-download",
  "title": "Download throughput by tile",
  "viewport": "geo",
  "mark": "polygon",
  "reduction": "rawPassthrough",
  "source": {
    "adapter": "clickhouse",
    "query": "SELECT quadkey, date, operator, kpi, value FROM colossus.crowdsource",
    "geometry": { "kind": "quadkey", "column": "quadkey" },
    "channels": [
      { "name": "value",    "column": "value",    "role": "measure",   "type": "f32" },
      { "name": "operator", "column": "operator", "role": "dimension", "type": "dict" },
      { "name": "kpi",      "column": "kpi",      "role": "dimension", "type": "dict" },
      { "name": "date",     "column": "date",     "role": "temporal",  "type": "date" }
    ]
  },
  "bakeFilters": ["date >= '2024-01-01'"],
  "filters": [
    { "channel": "kpi",      "control": "select",      "default": "download" },
    { "channel": "operator", "control": "multiSelect" },
    { "channel": "date",     "control": "dateRange" }
  ],
  "aggregate": { "by": ["quadkey"], "measures": { "value": "avg" }, "when": "client" },
  "storage": {
    "format": "parquet",
    "partitionBy": ["tile"],
    "sortBy": ["quadkey", "date"],
    "dictionary": ["kpi", "operator", "quadkey"],
    "bloom": ["quadkey"],
    "rowGroupRows": 131072,
    "compression": "zstd"
  },
  "encoding": { "color": { "channel": "value", "scheme": "viridis" } }
}
```

---

## Top-level fields

| Field           | Req | Type              | Notes |
|-----------------|-----|-------------------|-------|
| `id`            | ✅  | string            | Unique. Becomes `tiles/<id>/`, the URL slug, the config filename. Kebab-case. |
| `title`         |     | string            | Human label for the UI. |
| `viewport`      | ✅  | enum              | `geo` \| `orthographic`. **Only `geo` in scope now.** |
| `mark`          | ✅  | enum              | `point` **[S1]** \| `polygon` **[S3]** \| `line` \| `arc` \| `rect` \| `heat` \| `text`. |
| `reduction`     | ✅  | enum              | `quadtreeLod` \| `rawPassthrough` **[S1]** \| `signalM4` \| `aggregate` (later). Dispatched, never hardcoded. |
| `source`        | ✅  | object            | Where and how data is read + normalized. See below. |
| `bakeFilters`   |     | string[]          | SQL predicates AND-ed into the extract `WHERE`. Fixed at bake — re-bake to change. **[S1]** |
| `filters`       |     | Filter[]          | Interactive, client-side, no re-bake. **[S4]** |
| `aggregate`     |     | object            | Only when the chart *is* an aggregate (RULES R1 exception). **[S3/S4]** |
| `storage`       |     | object            | Physical Parquet layout hints. **[S4]** |
| `encoding`      |     | object            | Channel → visual channel (color/size/…). **[S2+]** |
| `schemaVersion` |     | int (default 1)   | Config schema version. |

## `source`

| Field       | Req | Type          | Notes |
|-------------|-----|---------------|-------|
| `adapter`   |     | string        | Which `ISourceAdapter`. Default `clickhouse`. |
| `query`     | ✅  | string        | **Any** SQL producing the columns referenced by `geometry`/`channels`. The bake wraps it: `FROM ( <query> )`. Do the joins/filters/`GROUP BY` you want here. |
| `geometry`  | ✅  | object        | The one spatial role. Tagged union on `kind`. |
| `channels`  | ✅  | Channel[]     | **Every other column**, each mapped to a typed role. |

### `geometry` (tagged union on `kind`)

| `kind`     | Extra fields          | Support | Normalizes to |
|------------|-----------------------|---------|---------------|
| `xy`       | `x`, `y`              | **[S1]** | point `(x, y)` |
| `lonLat`   | `lon`, `lat`         | **[S1]** | geo point `(x=lon, y=lat)` |
| `quadkey`  | `column`             | **[S3]** | tile polygon + centroid |
| `wkt`      | `column`, `geographic` | **[S3]** | polygon/line vertices + centroid |
| `geohash`  | `column`             | later   | cell polygon + centroid |
| `h3`       | `column`             | later   | hex polygon + centroid |

Every geometry yields a representative `(x, y)` — that is what the spatial sort, zone-maps, LOD, and
viewport query all run on, uniformly, regardless of shape.

### `channels[]`

Normalization is **not** geo-only: every non-geometry column is carried as a typed, encoded channel.

| Field    | Type | Notes |
|----------|------|-------|
| `name`   | string | Logical name referenced by `filters`/`aggregate`/`encoding`. |
| `column` | string | Source column/expression from `query`. |
| `role`   | enum | `measure` (continuous → ramps/range filters) \| `dimension` (low-card → color-by/equality filters, dict-encoded) \| `temporal` (date/time) \| `identity` (key for tooltips/joins). |
| `type`   | enum | `f32` \| `f64` \| `u8` \| `u16` \| `i32` \| `i64` \| `dict` \| `date`. |

A dimension is interactively filterable **iff** it is carried as a channel (see RULES R2). To keep
`kpi`/`operator`/`date` filterable, carry them — don't `GROUP BY` them away in the query.

## `filters[]` — interactive, client-side **[S4]**

| Field     | Type | Notes |
|-----------|------|-------|
| `channel` | string | Must name a carried channel. |
| `control` | enum | `select` \| `multiSelect` \| `dateRange` \| `range`. |
| `default` | string? | Optional initial value. |

## `aggregate` — the RULES R1 exception **[S3/S4]**

| Field      | Type | Notes |
|------------|------|-------|
| `by`       | string[] | Group keys (usually the geometry key, e.g. `quadkey`). |
| `measures` | map | `channel → fn` (`avg`, `sum`, `min`, `max`, `count`). |
| `when`     | enum | `client` (DuckDB-WASM recomputes per filter → stays interactive) \| `bake` (pre-aggregate to a tiny fixed store). |

## `storage` — physical Parquet layout **[S4]**

| Field          | Type | Default | Purpose |
|----------------|------|---------|---------|
| `format`       | string | `parquet` | `parquet` (queryable store) \| `arrow` (preview render tiles). |
| `partitionBy`  | string[] | `["tile"]` | File-level pruning (`tile` = spatial). |
| `sortBy`       | string[] | geometry order | Row-group zone-map locality. |
| `dictionary`   | string[] | low-card dims | Dictionary-encode for cheap equality pushdown. |
| `bloom`        | string[] | — | Bloom filters for high-cardinality equality (e.g. `quadkey`). |
| `rowGroupRows` | int | `131072` | Range-request granularity vs. pruning precision. |
| `compression`  | string | `zstd` | `zstd` \| `snappy` \| `none`. |

## `encoding` — data → visual channels **[S2+]**

| Field   | Shape | Notes |
|---------|-------|-------|
| `color` | `{ channel, scheme? }` | e.g. `viridis`. |
| `size`  | `{ channel }` | measure → radius/height. |

---

## Lifecycle

```
author/upload  →  views/<id>.json        (registry)
bake           →  dotnet run --project src/Colossus.Bake -- <id>
serve          →  GET /api/views/<id>/url →  open the returned URL
```

`GET /api/views` lists all registered views; `GET /api/views/<id>` returns the raw config;
`GET /api/views/<id>/url` returns the frontend deep-link that renders it.

## Enum quick reference (JSON is camelCase)

- `viewport`: `geo`, `orthographic`
- `mark`: `point`, `polygon`, `line`, `arc`, `rect`, `heat`, `text`
- `reduction`: `rawPassthrough`, `quadtreeLod`, `signalM4`, `aggregate`
- `geometry.kind`: `xy`, `lonLat`, `quadkey`, `wkt`, `geohash`, `h3`
- `channel.role`: `measure`, `dimension`, `temporal`, `identity`
- `channel.type`: `f32`, `f64`, `u8`, `u16`, `i32`, `i64`, `dict`, `date`
- `filter.control`: `select`, `multiSelect`, `dateRange`, `range`
- `aggregate.when`: `client`, `bake`
