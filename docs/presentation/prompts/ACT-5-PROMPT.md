# Prompt for Claude design — Act V (internal name only): Live aggregates over facts, slides 25–30

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as the previous
batches — include it with every paste. Numbering here already accounts for the three insert
slides (dictionary encoding, Hilbert sort, position-is-the-key), so this batch continues at 25
(slides 25–30).

Numbers in this batch are real measurements from the current builds — render them exactly as
given; the pre-talk checklist re-confirms them on the final builds.

---

Create slides 25–30 of the same technical engineering talk (Vodafone-branded lecture deck,
continuing from slides 1–24). The previous section ended with tile traffic tens of times
smaller; this section is the system's deepest feature — per-feature aggregates that recompute
live under every filter change, over facts the client never sees raw. The insert slide that
closed the previous section ("position IS the key") already introduced features, facts, and the
positional join — build directly on it. Junior-dev audience — define every term the first time
it matters.

## DESIGN SYSTEM (applies to every slide in this deck)

- 16:9, Vodafone brand theme: clean white background (#FFFFFF), dark grey body text (#4A4D4E),
  near-black titles (#25282B). Primary accent: Vodafone red (#E60000), used ONLY for emphasis
  and diagram highlights. Secondary accent: warm orange (#EB9700), used only where something
  "breaks" or costs. No other colors.
- Typography: the Vodafone typeface if available, otherwise a clean geometric sans; body text
  generous (min ~24pt equivalent); use a monospace face for anything that is code, a format
  name, a number with units, or a pipeline stage (e.g. `z/x/y`, `Float32Array`, `10⁹ rows`).
- One idea per slide. On-slide text stays terse — full sentences live in the presenter notes.
  Never render the NOTES section on the slide; put it in the speaker-notes field.
- The deck is organized into sections in these instructions, but NEVER render the word "Act",
  a section number, or any section label on a slide. No section-divider slides unless a spec
  explicitly asks for one.
- Diagrams: flat vector style, thin 1–2px strokes, no drop shadows, no 3D, no gradients. Label
  every arrow. Diagrams read left → right.
- Footer on every slide except the title: slide number, small, low-contrast.
- Tone of all copy: precise, calm, a little dry. It should read like a good lecture handout.

## SLIDE 25 — The problem: aggregates that follow the filter

ON SLIDE
- Header: **Drag a date range — every feature's aggregate recomputes**
- Geometry repeats: one polygon, measured per period per vendor — one **feature**, many
  **facts**.
- "Color by the dominant vendor *over the selected range*": change the range → recompute one
  aggregate **per feature** → recolor.
- Zoomed out, that is an aggregate over **millions of facts, per interaction**.
- Elsewhere: BI tools fire a query per interaction (round trip, seconds); OLAP cubes
  precompute every filter combination (combinatorial explosion — and an ad-hoc range isn't in
  the cube); rendering stacks simply don't (color is static).

VISUAL
- Left: a date-range slider mid-drag. Center: a patch of map polygons, each with a tiny `Σ`
  chip; thin arrows fan from the slider to every chip. Annotation under the fan, monospace:
  `millions of facts / interaction`. Right: a narrow orange strip listing the three
  "elsewhere" answers, each with its failure struck through in one line.

SPEAKER NOTES
- This is the promise from the vocabulary slide coming due — "one polygon, measured every
  month by four vendors" stops being a definition and becomes the workload. State the one
  consistent semantic rule of the whole system: a filter on a **per-feature** column is a GPU
  predicate — the two-uniform filter slide; a filter on a **per-fact** column re-scopes
  *every aggregate of every feature*. Same gesture in the UI, radically different machinery
  underneath. The budget this section must hit is the thesis picture's right-hand sliver: a
  small fetch plus a worker pass — never a database query.

## SLIDE 26 — Partial aggregates, and a grammar that compiles to plans

ON SLIDE
- Header: **Store partials once — recombine under any filter, exactly**
- The build stores, per (feature × dimension combination): `sum`, `count`, `min`, `max`,
  weighted sums — **partial aggregates**. Associative: any subset combines later **without
  the raw values**. Lossless and exact — no sketches, no sampling.
- On a filter change, a worker combines each feature's surviving partials, then finalizes:
  `avg = sum / count` · `share = part / whole` · `argmax` = the category with the winning sum.
- Declared in config: `wavg(download, tests)` · `share(sum(tests)) where vendor = 'A'` ·
  `argmax(vendor, sum(tests))`. It *reads* like SQL — it **compiles to a plan over partials;
  no database is ever contacted at runtime.**
- The main thread never aggregates: typed arrays end to end, in a worker.

VISUAL
- Three-stage flow, left → right: a feature's facts as a short row stack → partial boxes
  (`Σ`, `n`, `min`, `max`) merging pairwise into one, merge arrows labeled `combine` → three
  finalize chips (`avg`, `share`, `argmax`) in red. Below the flow, a database cylinder
  crossed out, captioned monospace: `runtime SQL: none`.

SPEAKER NOTES
- Teach associativity with the classic trap: a sum of sums is the sum; an average of averages
  is wrong — which is exactly why partials keep `sum` and `count` separately and divide only
  at finalize. `argmax` explains the demo the audience saw: the winner is picked from
  per-category sums *after* the filter is applied, so a date drag can flip a feature's winning
  vendor — that flip is the recolor. The grammar is a deliberate seduction: analysts read it
  as SQL, but it is a property of the view config that compiles to array plans — nothing is
  parsed, planned, or executed against a database at runtime. Segue: the next slide is where
  these partials physically live, and how the naive answer nearly priced the feature out.

## SLIDE 27 — Deep dive: keys become coordinates

ON SLIDE
- Header: **From key columns to a cell space**
- Naive sidecar storage — one row per (feature, dimension values), with key columns — prices
  out at scale: **gigabytes at rest, tens of MB per tile fetch — and roughly half of every
  row is repeated key material.**
- Instead, every per-fact dimension becomes an **axis**, with one of two algebras:
  **categorical** (filtered by equality; domain = its dictionary) or **ordered** (filtered by
  range; dates are the everyday case).
- The cross product of the axis domains is the tile's **cell space**: `4 vendors × 8 periods
  = 32 cells`.
- A fact's keys are now coordinates: `cellId = categoryCode · T + orderedBin`. The key
  columns vanish into array indexing.

VISUAL
- Left: a fat row table with its key columns shaded orange, annotated "half the bytes: keys,
  repeated per row". A red arrow labeled `becomes` points right: a 4 × 8 cell grid with one
  feature's facts as filled cells, axes labeled `vendor (categorical)` and `period (ordered)`.
  Beneath, monospace: `cellId = code · T + bin`, with one worked example arrow taking a
  (vendor, period) pair into its grid cell.

SPEAKER NOTES
- The dictionary-encoding slide planted this payoff: the canonical integer code stops being a
  label and becomes a *coordinate* — the axis position IS the code, so nothing is looked up,
  hashed, or remapped on the way into the grid. The build also **measures occupancy** —
  `facts / (features × cells)` — and records its physical-layout choice in the manifest; the
  client branches on the manifest and never sniffs bytes. Keep the two algebras crisp for the
  audience: equality vs range is the distinction the next slide's layouts are built around.

## SLIDE 28 — Two layouts, one gate: measured occupancy

ON SLIDE
- Header: **Dense planes with prefix sums, or sparse CSR — per tile, by measurement**
- **Dense** (high occupancy): per partial aggregate, one **plane** — a `cells × features` 2-D
  array, cell-major. Along the ordered axis, subtractable partials are stored as **prefix
  sums** (running totals): a range `[lo, hi]` becomes `cum[hi] − cum[lo−1]` — **two array
  reads per feature, O(1) in the range width**. (`min`/`max` can't subtract — stored raw,
  scanned.)
- **Sparse — CSR** ("compressed sparse row", the standard sparse-matrix layout):
  `offsets[features+1]` + `cellIds[nnz]` + one value array per partial. A filter compiles
  once to a `Uint8[cells]` bitmask, then one linear pass — no per-row key decode.
- The gate is **measured occupancy, per leaf tile** (`≥ 0.5` → dense), recorded in the
  manifest. Integer widths follow measured counts (`u8`/`u16`/`u32` cell ids); feature ids
  don't exist — the array index is the id.

VISUAL
- Left: the key-column table from the previous slide morphing into a stack of planes, one per
  partial aggregate; one **cell row** highlighted red across a plane; the prefix-sum
  subtraction drawn as two thin arrows dipping into the plane at `cum[hi]` and `cum[lo−1]`,
  meeting at a minus sign. Right: the CSR triplet as three short labeled strips. Between the
  two, a small diamond labeled `occupancy ≥ 0.5?` routing tile pictograms left and right.

SPEAKER NOTES
- Prefix sums earn the teaching beat of the section: store running totals instead of raw bins
  and any window collapses to one subtraction — the fencepost (`lo − 1`) is the only detail
  that ever trips anyone up. Why the gate is per tile, not per view: occupancy is heavily
  skewed — on the current reference builds only ~5% of leaf tiles clear the dense gate; a
  per-view choice would have forced everything sparse and foreclosed the slicing on the next
  slide. And the quiet payoff worth one sentence: a dense plane's cell row is contiguous
  bytes — literally a GPU texture row, uploadable without reshaping by a future GPU-side
  aggregation executor.

## SLIDE 29 — Fetch two cell rows, not a tile

ON SLIDE
- Header: **A date-range drag fetches two rows, not a tile**
- All sidecar data for a build version lives in **one archive** — the same trick as the tile
  archive: every block independently compressed with the trained-dictionary codec, and a
  directory mapping `tile → plane → cell row → byte range`. The client fetches HTTP ranges.
- **Per-plane ranges** — an interaction fetches only the planes its active aggregates read.
- **Cell-row slicing** — the ordered axis is innermost, so a range query needs exactly **two
  contiguous cell rows** (`hi` and `lo−1`) per selected category.
- Measured, worst dense tile, windowed interaction: whole-plane fetch **`1.88 MB` → `0.188 MB`
  sliced — `10×`**.
- Cache identity unchanged: `(version, tileKey)`; slices cache *under* their tile.

VISUAL
- One horizontal archive byte strip with three magnification brackets of increasing zoom:
  whole tile → one plane → one cell row, each bracket with its directory arrow and a byte
  figure shrinking left to right. The ratio `10×` large in red at the end. Caption, monospace:
  `a date-range drag fetches two rows, not a tile`.

SPEAKER NOTES
- Be honest about the trade: independently compressed small blocks lose cross-block
  redundancy, so slicing *costs* bytes at rest (~1.3× on sliced tiles — paid only on the
  dense minority, and the trained dictionary is what keeps it that low). The interaction win
  landed *above* the estimate on the re-measured build — 13× — because real block boundaries
  plus the dictionary compress tiny blocks better than the simulation predicted; the slide
  quotes the conservative figure, say the higher one out loud. A range with no categorical
  narrowing reads every category's rows and still wins, just less (~2–3×). Sparse tiles skip
  slicing entirely — their blocks are small by construction — and the manifest records which
  tiles support what.

## SLIDE 30 — The escape hatch: priced at build time, routed behind one seam

ON SLIDE
- Header: **Under budget → client. Over → server. Same interface.**
- Some views will exceed any client budget (cell space × features too large even sliced). So
  the build **prices** every view's worst per-interaction fetch — measured plane bytes, worst
  leaf tile plus a dense-screenful estimate — against a budget: `32 MB`.
- Over budget → aggregation routes to one small server endpoint: the **same declared
  aggregates**, executed by DuckDB **over the build's own Parquet artifacts** — the source
  database is still never contacted.
- The response is finalized columns in feature order: **~`4 B` per feature per aggregate** on
  the wire — no keys, no facts.
- Both routes sit behind the identical client seam: `aggregate(definitions, filters) →
  columns`. The renderer cannot tell where the computation ran. Tiles stay immutable static
  files; this endpoint is the only compute in the serve path.

VISUAL
- A decision diamond: `priced sidecar ≤ 32 MB?` — the "yes" arrow runs down-left through a
  worker pictogram; the "no" arrow down-right through a small server box chained to
  `DuckDB → build Parquet`, with a struck-through source-DB cylinder beside it. Both arrows
  terminate in one shared box, monospace: `aggregate(definitions, filters) → columns`, which
  feeds the renderer.

SPEAKER NOTES
- The budget is calibrated from measurement, not taste — the codec slide's lesson again. The
  measured worst leaf interaction on shipped views is ~8 MB, which the browser recomputes in
  tens of milliseconds — so real views stay client-side. An earlier 8 MB budget put a shipped
  view 1.6% over the line and would have swapped its ~50 ms client recompute for a ~4 s
  server round trip — evidence that 8 MB was never a real client limit, so the line moved to
  32 MB. The 4-bytes-per-feature figure is the positional join paying off one last time: the
  response is plain columns in feature order — no id column, nothing else. Close the section
  against the thesis picture: the aggregate-recompute bar is a small ranged fetch plus a
  worker pass — or one small response — and never, under any route, a query to the source
  database.
