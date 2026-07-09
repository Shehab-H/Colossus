# Colossus — View Configuration Reference

A **view** is a declarative JSON document. It is the *only* thing you author to add a visualization —
no code, no redeploy. Drop a file in `views/<id>.json` (or upload it via `POST /api/views`), bake it,
and it's live. This file documents every field.

The rules a view may never violate are in [RULES.md](RULES.md); the design + roadmap are in [PLAN.md](PLAN.md).

> **Status.** Fields are marked **✓ works** or **◦ planned** (roadmap in PLAN.md). Authoring a planned
> field is not an error — the bake ignores what it can't yet honor. Live today: the **geo** map; `point`
> and `polygon` marks; `xy` / `lonLat` / `quadkey` geometry; planner-chosen reduction; `bakeFilters`,
> `encoding.color` (the full scale system below), and `inspect`. Planned: interactive `filters`,
> `storage` (queryable store), `aggregate`, `size` encoding, and the remaining marks/geometries.

---

## Minimal example — points on a map

```json
{
  "id": "geo-points",
  "title": "Synthetic metro points",
  "viewport": "geo",
  "mark": "point",
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

## Target example — quadkey choropleth (uses planned `filters` / `storage`)

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
| `viewport`      | ✅  | enum              | `geo` ✓ \| `orthographic` (path exists; no non-geo view configured yet). |
| `mark`          | ✅  | enum              | `point` ✓ \| `polygon` ✓ \| `line` \| `arc` \| `rect` \| `heat` \| `text` ◦ planned. |
| `reduction`     |     | enum              | Optional hint, **ignored** — the bake planner picks from the data's shape. `quadtreeLod` \| `rawPassthrough` \| `aggregate` \| `signalM4`. |
| `source`        | ✅  | object            | Where and how data is read + normalized. See below. |
| `bakeFilters`   |     | string[]          | SQL predicates AND-ed into the extract `WHERE`. Fixed at bake — re-bake to change. ✓ |
| `filters`       |     | Filter[]          | Interactive, client-side, no re-bake. ◦ planned. |
| `aggregate`     |     | object            | Only when the chart *is* an aggregate (RULES R1 exception). ◦ planned. |
| `storage`       |     | object            | Physical queryable-store (Parquet) layout hints. ◦ planned. |
| `encoding`      |     | object            | Data channel → visual channel. `color` ✓ (full scale system, below); `size` ◦ planned. |
| `inspect`       |     | object            | Click-to-inspect panel. Nullable — omit and marks aren't pickable. ✓ See below. |
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
| `xy`       | `x`, `y`              | ✓ | point `(x, y)` |
| `lonLat`   | `lon`, `lat`         | ✓ | geo point `(x=lon, y=lat)` |
| `quadkey`  | `column`             | ✓ | tile polygon + centroid |
| `wkt`      | `column`, `geographic` | ◦ | polygon/line vertices + centroid |
| `geohash`  | `column`             | ◦ | cell polygon + centroid |
| `h3`       | `column`             | ◦ | hex polygon + centroid |

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

## `filters[]` — interactive, client-side ◦ planned

| Field     | Type | Notes |
|-----------|------|-------|
| `channel` | string | Must name a carried channel. |
| `control` | enum | `select` \| `multiSelect` \| `dateRange` \| `range`. |
| `default` | string? | Optional initial value. |

## `aggregate` — the RULES R1 exception ◦ planned

| Field      | Type | Notes |
|------------|------|-------|
| `by`       | string[] | Group keys (usually the geometry key, e.g. `quadkey`). |
| `measures` | map | `channel → fn` (`avg`, `sum`, `min`, `max`, `count`). |
| `when`     | enum | `client` (DuckDB-WASM recomputes per filter → stays interactive) \| `bake` (pre-aggregate to a tiny fixed store). |

## `storage` — physical queryable-store layout ◦ planned

| Field          | Type | Default | Purpose |
|----------------|------|---------|---------|
| `format`       | string | `parquet` | `parquet` (queryable store) \| `arrow` (preview render tiles). |
| `partitionBy`  | string[] | `["tile"]` | File-level pruning (`tile` = spatial). |
| `sortBy`       | string[] | geometry order | Row-group zone-map locality. |
| `dictionary`   | string[] | low-card dims | Dictionary-encode for cheap equality pushdown. |
| `bloom`        | string[] | — | Bloom filters for high-cardinality equality (e.g. `quadkey`). |
| `rowGroupRows` | int | `131072` | Range-request granularity vs. pruning precision. |
| `compression`  | string | `zstd` | `zstd` \| `snappy` \| `none`. |

## `encoding` — data → visual channels

### `color` — a scale spec (à la Vega-Lite)

`color` maps a data channel to color through a **scale**. Only `channel` is required; the client infers a
scale from the channel's datatype when the rest is omitted (numeric → `linear`, discrete → `categorical`).
`channel` may be **any** carried channel, of any type — not just a measure. The HUD's "color by" can switch
the channel at runtime; the scale re-derives from that channel's data.

| Field | Type | Applies to | Notes |
|-------|------|-----------|-------|
| `channel` | string | all | The channel to color by. Must be declared in `source.channels`. |
| `type` | enum | all | `linear` \| `log` \| `sqrt` \| `diverging` \| `quantize` \| `quantile` \| `threshold` \| `ordinal` \| `categorical`. Inferred if omitted. |
| `scheme` | string | all | Named ramp/palette (see below). Unknown → the family default. |
| `range` | string[] | all | Explicit hex list — overrides `scheme`. |
| `domain` | number[] \| any[] | all | Numeric `[min,max]` or explicit category order. Derived from data if omitted. |
| `reverse` | bool | continuous | Flip the ramp. |
| `midpoint` | number | `diverging` | The neutral center (default: domain midpoint). |
| `bins` | int | `quantize`/`quantile` | Bucket count. Must be > 0. |
| `thresholds` | number[] | `threshold` | Explicit break points → N+1 buckets. |
| `palette` | map | `categorical` | Explicit value → hex. A **closed set**: unmapped values get `unknown`. |
| `unknown` | string | all | Color for unmapped / null values (default gray). |

**Scale types by job.** *Magnitude* → `linear`/`log`/`sqrt` (continuous ramp) or `quantize`/`quantile`/`threshold`
(binned choropleth). *Polarity* (+/− around a baseline) → `diverging`. *Identity* (which category) → `categorical`.
*Order* (tiers, buckets) → `ordinal`. Binning and continuous scales work on any numeric channel and on any
reducer; **categorical color needs each mark to carry one category value** — i.e. point/`rawPassthrough`
marks, or an aggregate faceted to one category by a filter (an averaged choropleth cell has no single category).

**Schemes.** sequential: `viridis` (default), `plasma`, `magma`, `inferno`, `cividis`, `turbo`, `blues`,
`greens`, `oranges`, `greys`. diverging: `blueRed` (default), `redBlue`, `blueOrange`, `purpleGreen`, `spectral`.
categorical: `okabeIto` (default — colorblind-safe), `category`, `status`.

```json
// continuous, binned into 5 quantiles of a reversed magma ramp
"encoding": { "color": { "channel": "download_mbps", "type": "quantile", "bins": 5, "scheme": "magma", "reverse": true } }

// diverging around zero
"encoding": { "color": { "channel": "delta_mbps", "type": "diverging", "scheme": "blueRed", "midpoint": 0 } }

// categorical business palette (points), with a default for the long tail
"encoding": { "color": { "channel": "operator", "type": "categorical",
  "palette": { "Vodafone": "#e60000", "Orange": "#ff7900" }, "unknown": "#888888" } }
```

### `size`

| Field   | Shape | Notes |
|---------|-------|-------|
| `size`  | `{ channel }` | measure → radius/height. ◦ planned. |

## `inspect` — click-to-inspect panel

Nullable. When set, clicking a mark pins a panel showing that cell's channel values; omit it and marks
aren't pickable. Every named channel must be a carried channel present in the tile (RULES R2).

```json
"inspect": { "title": "download_mbps", "channels": ["download_mbps", "upload_mbps", "latency_ms"] }
```

| Field      | Type | Notes |
|------------|------|-------|
| `channels` | string[] | Channels shown, top to bottom. At least one; each must be a declared channel. |
| `title`    | string? | Optional channel whose value heads the panel (e.g. an id or the primary measure). |

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
