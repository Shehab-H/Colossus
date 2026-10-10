# Colossus — View Configuration (canonical DSL)

A **view** is a declarative JSON document. It is the *only* thing you author to add a visualization —
no code, no redeploy. Drop a file in `views/<id>.json` (or upload it via `POST /api/views`), bake it,
and it's live. **This file is the canonical definition of the config language**: every field, its
exact semantics, and — explicitly — what is supported today versus specified-but-not-yet-built.
The invariants a view may never violate are in [RULES.md](RULES.md).

> **Support legend.** Every field is marked:
> **✓ live** — works today, end to end. **◦ specified** — the semantics below are final and
> implementation must match them, but the engine does not honor the field yet (authoring it is not an
> error; the bake ignores what it can't honor). Anything not in this document is **unsupported** —
> there is no hidden behavior.

Live today: `geo` viewport; `point` + `polygon` marks; `xy` / `lonLat` / `quadkey` geometry;
planner-chosen reduction; `bakeFilters`; auto-derived interactive filters over dimension/temporal
channels (single-select + date range); the full `encoding.color` scale system; `inspect`.
Specified, not yet built: **the group/measure model** (`measures`, computed color — this repo's
active direction), curated `filters[]`, `storage`, `size`, remaining marks/geometries.

---

## 1. The data model: marks, facts, measures

This section is normative — it defines what a row *is*. Everything else hangs off it.

**A mark is one distinct geometry.** The `source.query` yields rows; rows sharing the same geometry
value belong to the same mark. Two regimes follow:

- **Row regime (✓ live).** Geometry is unique in the source — every mark has exactly one row, every
  channel has exactly one value per mark. This is every view without a `measures` block, and it is
  exactly the engine's current behavior. Nothing about this regime changes, ever, without a
  schemaVersion bump: **a view with no `measures` block behaves byte-for-byte as today.**
- **Group regime (◦ specified).** Geometry repeats — e.g. `(quadkey, quarter, operator, kpi…)`
  facts. The rows of a mark are its **facts**. Per-mark values then only exist as **measures**:
  declared aggregate expressions evaluated over the mark's facts *that survive the active filters*.
  Declaring a `measures` block is what opts a view into this regime.

**Channel classification is derived, not authored.** At bake time, the probe classifies every
channel as `perMark` (single value per mark — a mark attribute) or `perFact` (varies within a mark).
The manifest records the classification; the client obeys it. Authors cannot get it wrong.

**Filter semantics (normative).** Every active filter applies to the whole view, in the way its
classification dictates:

- A filter on a **perMark** channel is a *predicate*: marks are in or out. (Executed GPU-side.)
- A filter on a **perFact** channel is *context*: it selects which facts contribute to **every**
  measure of **every** mark. If a filter reduces a mark's surviving facts, all of that mark's
  measures — including the one driving color — recompute over the survivors and the mark recolors.
  There are no exceptions and no per-measure opt-outs: measures are always evaluated over the
  intersection of all active filters (plus the measure's own `where`, § 4).
- A mark whose surviving fact set is empty is not drawn while the context filter is active — it
  disappears exactly like a predicate-filtered mark. Its geometry is still baked; it returns the
  moment a filter change lets facts survive again.

**Execution is engine-internal and never touches the source database.** Measures are folded
client-side over baked partial aggregates (worker or GPU executor), with a planner-selected
server-side fold over the *baked* artifacts as fallback for views whose declared grid exceeds
client budgets. The expression grammar below is deliberately query-shaped so authors think in
familiar terms, **but it is not SQL and is never sent anywhere**: it compiles to fold plans over
baked partials. The source DB's only job remains answering `source.query` once, at bake.

## 2. Minimal example — row regime (✓ live)

```json
{
  "id": "geo-points",
  "viewport": "geo",
  "mark": "point",
  "source": {
    "adapter": "clickhouse",
    "query": "SELECT lon, lat, value, category FROM colossus.points_geo",
    "geometry": { "kind": "lonLat", "lon": "lon", "lat": "lat" },
    "channels": [
      { "name": "value",    "column": "value",    "role": "measure",   "type": "f32" },
      { "name": "category", "column": "category", "role": "dimension", "type": "dict" }
    ]
  }
}
```

## 3. Flagship example — group regime (◦ specified; dataset is live: `views/mobile-coverage.json` runs the row-regime version today)

Non-unique geometry: each z14 quadkey repeats per (quarter, operator). Color by the dominant
operator over whatever date range is selected — the archetypal filter-dependent computed color.

```json
{
  "id": "mobile-dominance",
  "title": "Dominant operator by tile",
  "viewport": "geo",
  "mark": "polygon",
  "source": {
    "adapter": "clickhouse",
    "query": "SELECT quadkey, quarter, operator, toFloat32(tests) AS tests, download_mbps FROM colossus.mobile_coverage",
    "geometry": { "kind": "quadkey", "column": "quadkey" },
    "channels": [
      { "name": "operator", "column": "operator", "role": "dimension", "type": "dict" },
      { "name": "quarter",  "column": "quarter",  "role": "temporal",  "type": "date" },
      { "name": "tests",    "column": "tests",    "role": "measure",   "type": "f32" },
      { "name": "download_mbps", "column": "download_mbps", "role": "measure", "type": "f32" }
    ]
  },
  "measures": [
    { "name": "total_tests",       "expr": "sum(tests)" },
    { "name": "avg_download",      "expr": "wavg(download_mbps, tests)" },
    { "name": "apex_share",        "expr": "share(sum(tests)) where operator = 'apex'" },
    { "name": "dominant_operator", "expr": "argmax(operator, sum(tests))" }
  ],
  "encoding": { "color": { "channel": "dominant_operator", "type": "categorical" } },
  "inspect": { "title": "dominant_operator",
               "channels": ["dominant_operator", "total_tests", "avg_download", "apex_share"] }
}
```

Selecting quarters `2025-01-01..2025-10-01` recomputes every tile's `sum(tests)` per operator over
those quarters only; tiles whose argmax flips recolor. Adding `operator = 'apex'` as a filter turns
the same map into apex's own footprint (all measures fold over apex facts only).

---

## 4. `measures[]` — computed per-mark values (◦ specified)

Only meaningful in the group regime (and what activates it). Each measure becomes a virtual
channel: colorable, inspectable, legend-able — indistinguishable downstream from a carried channel
except that its value is a function of the active filters.

**The `GROUP BY` is implicit and fixed: every measure is grouped by the mark (the geometry).**
There is exactly one grouping in the whole model, ever — `sum(tests)` always means "sum of tests
*per geometry*, over its facts that survive the active filters." You never author a group key;
declaring a different one is not possible. (`argmax(operator, sum(tests))` has an inner
sub-grouping by `operator`, but only *within* each mark — the output is still one value per mark.)
If you want a different grain than the geometry, that's a different view with a different
`source.query`.

| Field  | Req | Notes |
|--------|-----|-------|
| `name` | ✅ | Virtual channel name. Must not collide with a `source.channels` name. |
| `expr` | ✅ | One expression from the closed grammar below. |

### Expression grammar (closed — this list is exhaustive)

```
expr     := agg | argext
agg      := verb '(' args ')' [ 'where' channel '=' literal ]
verb     := sum | count | avg | wavg | min | max | share
argext   := ('argmax' | 'argmin') '(' dimension ',' agg ')'
```

| Expression | Output | Semantics over the mark's surviving facts |
|---|---|---|
| `sum(ch)` | numeric | Σ ch |
| `count()` | numeric | surviving fact count |
| `avg(ch)` | numeric | Σ ch / count |
| `wavg(ch, w)` | numeric | Σ (ch·w) / Σ w |
| `min(ch)` / `max(ch)` | numeric | extremum of ch |
| `share(agg)` + `where` | numeric 0..1 | agg restricted by `where`, divided by the same agg unrestricted (the `where` is what makes it a share) |
| `argmax(dim, agg)` / `argmin(dim, agg)` | **categorical** (dim's domain) | the `dim` value whose group extremizes the inner agg |
| `… where ch = 'v'` | — | modifier on any `agg`: restrict to facts where the (perFact, dict) channel equals the literal, *before* the fold |

Rules: `ch`/`w` are numeric channels; `dim` and `where`-channels are dict channels classified
perFact; one aggregate verb per measure; `argmax`'s inner agg may not itself carry a `where`.

### Explicitly unsupported (validation errors, with these reasons)

- **Arithmetic between measures** (`sum(a) / sum(b)`) — not in v1; `wavg` and `share` cover the
  known ratio cases. Revisit only with a real view that needs it.
- **`countDistinct`, `median`, quantiles** — not decomposable into foldable partials. Future path:
  mergeable sketches (HLL / t-digest) as partial payloads, or the server fold. Rejected until then.
- **Nested aggregates, window semantics, arbitrary SQL, subqueries** — the grammar is a fold plan,
  not a query language. If it can't fold, it doesn't belong here; put it in `source.query` (bake
  time) instead.
- **Measures over perMark channels** — meaningless (one value per mark); use the channel directly.

### Bake obligations (what `measures` makes the bake produce)

Per tile, alongside geometry: sparse partial-aggregate companions at the grain
`(mark, perFact-dict channels named by any measure/filter, temporal bins)`, carrying exactly the
partials the declared verbs need (`sum`→sum, `avg`→sum+count, `wavg`→Σch·w+Σw, `min/max`→min/max).
Temporal bins are the temporal channel's distinct values (a `bin` field for high-cardinality time
is future work, not specified here). The planner prices `marks × bins × dims` per tile and routes
the view to the client fold or the server fold; both are engine-internal. Companion size and route
appear in the bake log.

## 5. Top-level fields

| Field | Req | Status | Notes |
|---|---|---|---|
| `id` | ✅ | ✓ | Unique, kebab-case. Becomes `tiles/<id>/` and the URL slug. |
| `title` | | ✓ | Human label. |
| `viewport` | ✅ | ✓ | `geo` ✓ · `orthographic` (path exists, no configured view yet). |
| `mark` | ✅ | ✓ | `point` ✓ · `polygon` ✓ · `line`/`arc`/`rect`/`heat`/`text` ◦. |
| `reduction` | | ✓ | Optional hint, **ignored** — the planner chooses from the data's shape. |
| `source` | ✅ | ✓ | § 6. |
| `bakeFilters` | | ✓ | SQL predicates AND-ed into the extract `WHERE`. Fixed at bake. |
| `measures` | | ◦ | § 4. Presence = group regime. |
| `filters` | | ◦ | § 7. Absent = every dimension/temporal channel auto-gets a control (✓ current behavior). Predicate filters execute GPU-side (✓ live). |
| `storage` | | ◦ | Parquet queryable-store layout (RULES R4/S4). Unchanged from prior spec; § 9. |
| `encoding` | | ✓ | § 8. `color` ✓ (full scale system) · `size` ◦. |
| `inspect` | | ✓ | § 10. Omit → marks not pickable. |
| `schemaVersion` | | ✓ | Default 1. The group regime does not bump it (purely additive). |

## 6. `source`

| Field | Req | Notes |
|---|---|---|
| `adapter` | | Which `ISourceAdapter`. Default `clickhouse`. Postgres/MySQL/files: future adapters, same contract. |
| `query` | ✅ | **Any** SQL producing the columns `geometry`/`channels` reference. The bake wraps it as a subquery. Joins, casts, bake-time `GROUP BY` — all here. |
| `geometry` | ✅ | Tagged union on `kind`: `xy` ✓ · `lonLat` ✓ · `quadkey` ✓ · `wkt`/`geohash`/`h3` ◦. Every kind yields a representative `(x, y)` (RULES R3). |
| `channels` | ✅ | Every non-geometry column, typed: `name`, `column`, `role` (`measure` · `dimension` · `temporal` · `identity`), `type` (`f32` `f64` `u8` `u16` `i32` `i64` `dict` `date`). |

Carry what you want to filter or aggregate on: a channel `GROUP BY`-ed away in `query` is gone.
In the group regime, carry fact columns raw — the *engine* owns interactive aggregation; `query`
owns only bake-time shaping.

## 7. `filters[]` (◦ specified; auto-derived controls are ✓ live)

When absent, every `dimension`/`temporal` channel gets a default control — exactly today's HUD.
Predicate (perMark) filters execute GPU-side via `DataFilterExtension` (✓ live): a filter
change updates only uniforms, touching no tile bytes; the tile identity never includes the filter.
When present, it curates which channels get controls and how:

| Field | Notes |
|---|---|
| `channel` | A carried channel (never a measure — measures are outputs, not inputs). |
| `control` | `select` ✓(as default behavior) · `dateRange` ✓(as default behavior) · `multiSelect` ◦ · `range` ◦. |
| `default` | Optional initial value (`"v"`, or `"from..to"` for dateRange). |

Whether a filter acts as predicate or context is **not configurable** — it follows the channel's
derived perMark/perFact classification (§ 1).

## 8. `encoding.color` — unchanged scale system (✓ live), one addition

`channel` may name a carried channel **or a measure** (◦ until the group regime lands). Everything
else is as before and live today: `type` (`linear` `log` `sqrt` `diverging` `quantize` `quantile`
`threshold` `ordinal` `categorical`, inferred from datatype when omitted), `scheme`, `range`,
`domain`, `reverse`, `midpoint`, `bins`, `thresholds`, `palette`, `unknown`. Sequential schemes:
`viridis` (default) `plasma` `magma` `inferno` `cividis` `turbo` `blues` `greens` `oranges` `greys`;
diverging: `blueRed` (default) `redBlue` `blueOrange` `purpleGreen` `spectral`; categorical:
`okabeIto` (default, colorblind-safe) `category` `status`.

Group-regime notes: `argmax` measures color as categorical over the dimension's baked domain;
numeric measures derive their domain from the *default context* at bake (the whole fact set), so
the scale stays stable while filters change values — a filtered value outside the baked domain
clamps. `unknown` renders null/out-of-domain values at the default context; a mark whose surviving
fact set is empty under an active filter is discarded instead (§ 1).

## 9. `storage` (◦ specified, unchanged)

Physical layout of the Parquet queryable store (RULES R4/S4): `format`, `partitionBy`, `sortBy`,
`dictionary`, `bloom`, `rowGroupRows`, `compression`. Semantics as previously specified; not
required by the group regime (partial companions are an engine artifact, not authored storage).

## 10. `inspect` (✓ live; group-regime addition ◦)

`channels`: what the click panel shows, top to bottom; `title`: optional heading channel. Every
name must be a carried channel — or, in the group regime, a measure (shown at its current filtered
value). Naming a perFact raw channel in a group-regime view is a validation error — wrap it in a
measure instead (there is no single raw value to show).

## 11. Validation (what the bake must reject, loudly)

- A measure name colliding with a channel name; an `expr` outside the grammar; `argmax` over a
  non-dict or perMark channel; `where` on a non-dict/perMark channel; measures in a view whose
  geometry never repeats (warning, not error — the group regime degenerates cleanly).
- `encoding.color.channel` / `inspect.channels` naming something that is neither a channel nor a
  measure.
- A `filters[].channel` naming a measure.
- Group-regime views whose priced companion grid exceeds both client and server fold budgets —
  the error names the offending channels and their cardinalities.

## 12. Lifecycle & enum quick reference

```
author/upload → views/<id>.json → dotnet run --project src/Colossus.Bake -- <id> → GET /api/views/<id>/url
```

- `viewport`: `geo`, `orthographic` · `mark`: `point`, `polygon`, `line`, `arc`, `rect`, `heat`, `text`
- `geometry.kind`: `xy`, `lonLat`, `quadkey`, `wkt`, `geohash`, `h3`
- `channel.role`: `measure`, `dimension`, `temporal`, `identity`
- `channel.type`: `f32`, `f64`, `u8`, `u16`, `i32`, `i64`, `dict`, `date`
- `measure verbs`: `sum`, `count`, `avg`, `wavg`, `min`, `max`, `share`, `argmax`, `argmin` (+ `where ch = 'v'`)
- `filter.control`: `select`, `multiSelect`, `dateRange`, `range`
