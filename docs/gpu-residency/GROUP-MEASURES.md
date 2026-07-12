# Group/Measure Model — Complete Implementation (v0 → GPU)

**Status: step 4 of the one-pass build (see README) — executed after Phases 1–3.** The numbered
phases are its substrate: Phase 1 executes its predicate filters, Phase 2 is the seam folded values
land in, Phase 5.3 is its eventual GPU fold. Semantics are normative in
[VIEW_CONFIG.md](../VIEW_CONFIG.md) §1/§4 — this file is *how*, that file is *what*. With Phases
1–2 already landed, "recolor" below means: folded values become the `getScaleValue` attribute
(numeric) or LUT codes (argmax) — not the legacy CPU color path, which no longer exists by then.

## The chain (all executors share it)

```
source.query (facts) ──bake──> marks staging (GROUP BY geometry key: default-context measure
                               values as ordinary channels)  ──existing reducers──> render tiles
                └──bake──> per-tile fact companions (sparse partials at grain)  z/x/y.facts.arrow

client: no context filter  → render baked default-context values (zero new work)
        context filter on  → fold(companion, measures, context) → per-mark values → recolor
```

Two facts make this work:

1. **Partials are additive**, so the *same* grid-cell grouping the aggregate reducer applies to
   sub-pixel marks applies to their facts — companions exist for internal (zoomed-out) tiles too,
   and folding them is exactly correct at every LOD.
2. **Default context = all facts**, so baked measure channels and a fold with no filters agree by
   construction; the baked values are just the fold's memoized zero-filter case (and they give the
   domain scanner real columns to scan → stable scales while filters change values).

## Why the marks table is not optional

The aggregate reducer's `ContentSql` carries **only measure-role channels** — merged sub-pixel
cells average measures and must drop dimensions. Therefore a fact table pushed straight through it
(today's row-regime `mobile-coverage`) produces tiles with no `operator`/`quarter` columns *and*
N overlapping copies of each geometry. Grouping to marks first fixes both: one geometry per mark,
and fact dims exist only where they're meaningful (companions).

## v0 scope (this implementation)

| In | Out (deferred, designed for) |
|---|---|
| `measures` on ViewConfig, full grammar parse + §11 validation (C# + TS mirror) | GPU fold (Phase 5.3 scatter-blend / prefix-sum — the executors swap behind the same fold interface) |
| Geometry kinds: `quadkey` (the key column IS the mark key) | keyed `wkt`/`geohash`/`h3` (need an authored id), point marks |
| Reduction: `aggregate` (planner outcome for polygon/large). Other reducers + measures → bake error | quadtree/raw group regime |
| CPU fold on the client (chunked; worker pool if profiling demands) | server DuckDB fold over baked Parquet (planner-routed) |
| Companions for **all** tiles (internal via grid-cell synthetic keys) | companion size pricing/routing in the planner |
| Filters: perFact dims (equality) + temporal ranges as context; perMark predicates unchanged | `multiSelect`/numeric `range` controls |

## Bake half

### 1. Config model (`Colossus.Domain`)

- `MeasureSpec(Name, Expr)` list on `ViewConfig` (JSON `measures`, optional — absence = row regime,
  byte-for-byte today).
- `MeasureExpr` parser (pure, `Colossus.Domain.Measures`): grammar exactly VIEW_CONFIG §4 —
  `verb(args) [where ch = 'lit']`, `argmax|argmin(dim, inner)`. Produces a typed AST:
  `Sum(ch) | Count | Avg(ch) | Wavg(ch,w) | Min(ch) | Max(ch) | Share(inner, whereCh, whereVal) |
  ArgExt(dim, inner, max)`. Validation errors name the measure and the offending token.
- Validation (bake-time, VIEW_CONFIG §11): name collisions, verb args must be numeric channels,
  `dim`/`where` must be dict channels, geometry must be keyed (v0: quadkey), reducer must be
  aggregate, color/inspect names resolve against channels ∪ measures.

### 2. Fact grouping (port + adapter)

- Port `IFactGrouper` (Domain.Baking): `string GroupToMarks(factsParquet, view)` → marks parquet.
- Adapter `DuckDbFactGrouper` (Infrastructure): one `GROUP BY <key>` over staging emitting:
  `key AS id` (dict), `first(geometry)`, `first(part_offsets)`, `first(x)`, `first(y)`, every
  perMark channel as `first(ch)`, and each measure at default context via DuckDB SQL:
  `sum(ch)` · `count(*)` · `avg(ch)` · `sum(ch*w)/sum(w)` · `min/max` ·
  `sum(ch) FILTER (where d='v') / sum(ch)` · `arg_max(dim, agg)` — rendered from the AST.
- perMark/perFact classification, same pass: `count(DISTINCT ch)` grouped by key — a channel is
  perFact iff any group has >1 distinct value. Recorded in the manifest (`factChannels`).

### 3. Effective render view

The reducers and domain scanner receive an **effective view** built in Application: channels =
perMark channels + one channel per measure (numeric → role `measure`/f32; `argmax` → role
`dimension`/dict) + `id` (identity, dict). The authored view goes into the manifest untouched.
Reducer change (AggregateReducer only): merged sub-pixel cells aggregate dict channels with
`mode()` and `id` as NULL (a merged mark has no single identity; the client folds it via its grid
key instead — below).

### 4. Companions (AggregateReducer)

Alongside each level-z tile write, a second `WritePartitioned` over the **facts** table produces
`<z>/<x>/<y>.facts.arrow` with grain `(mki, <temporal channels>, <perFact dict channels>)`:

- `mki`: the row index of the fact's mark **within the render tile** (Int32). The reducer
  materializes the level's content with `row_number() OVER (PARTITION BY tx, ty ORDER BY id)` and
  the companion joins it on the mark key (the geometry key for real marks, `'g:'||gx||':'||gy` for
  facts whose mark merged at this z — same CASE both sides), so alignment holds by construction.
  The client fold is then an O(1) integer gather — no mark-key strings ship to the client at all
  (they were the largest companion column and the fold's per-row hash lookup).
- One column per grain channel, by its channel name (dict / DATE).
- Partial columns, deterministically named (client mirrors this exactly):
  `sum__<ch>` · `cnt` · `swp__<ch>__<w>` (Σ ch·w) · `min__<ch>` · `max__<ch>` — the union of what
  the parsed measures need; `avg`→`sum__<ch>`+`cnt`, `wavg`→`swp__…`+`sum__<w>`, `share`/`argmax`
  → their inner agg's columns (their dims are already grain).
- Manifest gains: `measures` (echoed authored), `factChannels` (classification),
  `companionTiles: true`, `grainChannels`.

### 5. Domains

Group regime scans **two** stagings: marks parquet for measure + perMark domains (scale stability:
numeric measure domains are the default-context min/max; argmax domains are the dim's values);
facts parquet for perFact dims/temporal (filter options).

## Client half

### 6. Types & classification (`manifest.ts`, `channels.ts`)

`ViewConfig.measures?: {name, expr}[]`; `Manifest.factChannels?`, `companionTiles?`,
`grainChannels?`. Helpers: `isGroupRegime(view)`, `measureNames(view)`;
`colorableChannels` → measures + perMark channels in group regime; `activeFilters` splits into
`predicateFilters` (perMark — go to tile decode exactly as today) and `contextFilters` (perFact —
**never** reach decode; they feed the fold). This split is what un-blanks filtering on
aggregate-reducer tiles.

### 7. Expression mirror + fold engine (`lib/measures.ts`, pure, unit-tested)

- `parseMeasure(expr)` — same grammar, same AST, same errors (fixture-shared with the C# tests:
  `tests/fixtures/measure-cases.json`, both sides pin it — the third cross-language authority,
  alongside tiling and schema).
- `foldTile(companion, measures, context): Record<name, Float32Array | Uint16Array>` — one pass
  over companion cells: skip cells failing context (temporal day-range on the day-number column,
  equality on dim codes), accumulate partials per (mki, argmax-dim) then finalize per measure
  (`avg=sum/cnt`, `wavg=swp/sum`, `share=restricted/unrestricted`, `argmax` → code into the dim's
  canonical domain). Mark alignment is the companion's own `mki` column — typed-array arithmetic
  end to end, no strings, no hashing, no per-row allocation.
- Companion fetch + decode runs on the tile worker pool (`tileLoader.loadCompanion`): the decoded
  form is all typed arrays (dict codes, day numbers, f32 partials) and transfers back zero-copy.
  Companions cache per `version|tileKey`; fold results cache per `(version, tileKey, contextSig)`,
  so pan/zoom under a fixed context re-folds nothing — only new tiles or a new context fold.

### 8. Rendering integration

- Tile decode: `readFields` also extracts `id` when the view is group regime.
- `useViewData`: color-by options include measures; `describeColorDomain` answers measures from
  baked `channelDomains` (they're channels in the marks staging, so the scanner already covered
  them).
- New hook `useMeasureFold(manifest, rendered, contextFilters)` → per-tileKey folded columns, or
  `null` when no context filter is active (render baked channels — zero cost).
- `tileDeckData(d, channel, colorOf, scaleKey, override?)`: when coloring by a measure under active
  context, the folded column overrides the baked one; the memo key gains the context signature so
  cached buffers per context are reused while scrubbing back and forth.
- Inspect: measure rows read folded values when present, baked otherwise.

## Verification (v0 definition of done)

1. `dotnet test` (parser, grouper round-trip through in-memory DuckDB, companion round-trip,
   fixture conformance) and `web` `tsc`/`oxlint`/`vitest` (parser mirror + fixture, fold engine,
   filter split) — green.
2. `views/mobile-dominance.json` bakes: marks pyramid has **one mark per quadkey** (leaf rows ≈
   627k, not 7.6M), companions exist for every tile, fidelity harness still passes for the two
   pre-existing views. (Note: `Σ leaves == source` counts *marks* in group regime; the honest
   invariant becomes `Σ leaf marks == distinct keys` + `Σ companion facts == source rows` — the
   verifier learns the second check.)
3. Live in the browser: dominant-operator map renders; narrowing the quarter range recolors tiles
   (38% flip across the full range); selecting `operator = apex` turns the map into apex's
   footprint via `share`/measure recompute; click-inspect shows folded values; `geonames` and
   `ookla-fixed` render exactly as before.

## The upgrade path (already designed, do not build now)

CPU fold → worker-pool fold (same function, transferables) → GPU fold: companions upload once as
GPU cells, context becomes uniforms, scatter-blend group-by + argmax MRT pass (Phase 5.3/5.4),
prefix-sum textures accelerate the temporal axis. Server DuckDB fold slots behind the same
`fold(measures, context) → columns` seam when the planner's companion pricing says the client
shouldn't carry a view's grid. Nothing in v0's artifacts changes for any of these — only the
executor.
