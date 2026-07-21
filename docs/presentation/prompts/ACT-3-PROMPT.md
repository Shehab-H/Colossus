# Prompt for Claude design — Act III (internal name only): The offline build, slides 13–17

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as the previous
batches — include it with every paste. Slides continue numbering (13–17).

---

Create slides 13–17 of the same technical engineering talk (Vodafone-branded lecture deck,
continuing from slides 1–12). The previous section showed how tiles become pixels; this section
shows how billions of source rows become those tiles in the first place — the offline build.
Junior-dev audience — define every term the first time it matters.

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

## SLIDE 13 — The problem: sources are messy; the pipeline must not be

ON SLIDE
- Header: **Any database, any geometry — one canonical schema**
- Real sources: any SQL database; geometry arrives as lon/lat pairs, map-grid cell keys, WKT
  polygons…
- Elsewhere: a pipeline per source, a code path per geometry type — format details leak all the
  way to the client.
- Ours: the source is **an arbitrary SQL query behind a pluggable adapter**. The adapter owns
  every dialect and geometry specific and normalizes rows into one canonical schema during
  extraction. Nothing downstream ever learns what kind of source existed.
- The unifying trick: every feature gets a representative `(x, y)` — so one set of spatial
  machinery serves points, polygons, and grid cells identically.

VISUAL
- Left: three mismatched source cards ("SQL + lon/lat", "SQL + grid-cell keys", "SQL + WKT
  polygons"). All three feed into one box labeled `adapter` (red outline), which emits one clean
  uniform row strip labeled "canonical schema: x, y, geometry, dimensions, measures". A dashed
  vertical line after the adapter labeled "source specifics end here".

SPEAKER NOTES
- The extraction also *sorts*: rows are ordered by a space-filling curve (a Hilbert curve — a
  path that visits every cell of a grid so that nearby cells are visited at nearby times), so
  spatially close features sit close together in every file. That one ordering decision quietly
  pays for itself twice later in the talk — file locality now, and compression in the next
  section. Plant it, don't explain it fully yet.

## SLIDE 14 — The spatial pyramid

ON SLIDE
- Header: **The working set is the screen, not the dataset**
- Tiles form a quadtree pyramid over the data's bounding box: zoom `z` has `4^z` tiles; each
  tile splits into 4 at `z+1`.
- The client fetches only the tiles intersecting the viewport at the current zoom.
- Consequence: what the browser holds is proportional to **pixels on screen** — not to rows in
  the database. That is the entire answer to "billions of rows".

VISUAL
- A three-level pyramid of tile grids (1 → 4 → 16), drawn in light grey; a viewport rectangle
  slicing through the middle level with the ~6 intersected tiles filled red. A small annotation
  arrow: "fetched: 6 tiles" vs a struck-through "not fetched: everything else".

SPEAKER NOTES
- This idea is borrowed proudly from web cartography — every slippy map works this way. The
  difference, coming on the next two slides, is what's *inside* the tiles: map tiles simplify
  and drop features to stay small; ours must not lie. Also note the pyramid is adaptive: a
  branch stops subdividing as soon as a tile is under budget, so sparse ocean costs nothing
  while dense cities go deep.

## SLIDE 15 — The build plans from the data, not from the chart

ON SLIDE
- Header: **The build probes the source and picks a strategy**
- One probe: how many rows, their extent, how many *distinct shapes*.
- Three strategies, chosen from shape — never authored, never named after a chart:
  - everything fits one tile budget → **ship it as-is** (one tile, done)
  - an area chart, or many rows per shape (a fact table over few geometries) → **aggregate
    pyramid**: coarse levels are honest means of children
  - a genuine point cloud (≈1 row per shape, more than fits) → **level-of-detail pyramid**:
    coarse levels are a labeled preview of real rows
- Budget: `250,000` features per leaf tile. Depth follows *distinct shapes*, not row count.

VISUAL
- A decision flow, top to bottom: probe box → three branches, each ending in a small pyramid
  pictogram (single tile / pyramid with Σ / pyramid with dots). Under each branch its one-line
  trigger condition in monospace (e.g. `rows ≤ budget`, `rows per shape ≥ 4`, `≈1 row per
  shape`).

SPEAKER NOTES
- The hard rule behind this slide: engine code never names a dataset, a column, or a shape.
  Every choice is computed from measured properties of the data — which is why pointing the
  system at a brand-new table requires authoring a config, not writing code. The "rows per
  shape" test is worth a beat: a table with 4+ rows per geometry is a fact table (think: one
  polygon measured monthly by several vendors) — rendering it as raw points would draw the same
  shape on top of itself; only aggregation makes sense, so that's what the build picks.

## SLIDE 16 — Coarse tiles that don't lie

ON SLIDE
- Header: **Level of detail without dropping the truth**
- A tile over budget keeps **one representative row per occupied ~1-pixel grid cell** — a real
  row, tagged with the count of rows it stands for — then subdivides.
- Only sub-pixel overlap is ever folded: neighboring tiles can't develop density cliffs.
- Every source row lands in **exactly one leaf**. Zooming in always resolves the preview into
  the real thing.
- No invented geometry, no synthetic "average" features — ever.

VISUAL
- Left: a dense scatter over a faint pixel grid; one cell magnified showing 7 dots collapsing
  to 1 red dot labeled `stands for 7`. Right: the same region one zoom level deeper, all 7 dots
  present. An equation strip along the bottom, monospace: `Σ leaf rows = source rows` with a
  checkmark.

SPEAKER NOTES
- Contrast with cartographic tiles explicitly: tippecanoe-style tooling *discards* features to
  hit tile size, which is correct for a road map and disqualifying for data. Here the only
  thing a coarse tile hides is what your screen physically could not show anyway — two rows in
  the same pixel. The equation on the slide is not aspiration, it's a check the build runs;
  the trust section at the end shows how it once caught a real double-counting bug.

## SLIDE 17 — The build end to end, and versions that flip atomically

ON SLIDE
- Header: **One directed pipeline, one atomic switch**
- `SQL query → adapter extract → staging (Parquet) → per-level reduce & partition (DuckDB) →
  tessellate → Arrow IPC tiles + manifest → version flip`
- Every build writes a fresh immutable `version/` directory; a tiny pointer file flips
  atomically. Readers never see a half-written build. Rollback = flip back.
- Serving after that: **static files, full stop.** No app server in the render path.

VISUAL
- The pipeline as a horizontal chain of stage boxes (monospace labels). Below it, two version
  directories (`v1/ v2/`) with a pointer arrow swinging from v1 to v2, labeled "atomic".
  Highlight in red only the pointer swing — the moment users switch.

SPEAKER NOTES
- Credit the workhorse: DuckDB, an embedded analytical database, runs the heavy per-level
  aggregation out-of-core — the build machine never needs the dataset in RAM. Staging is
  Parquet because at build time the job IS scanning — the format trade from the render-path
  section, running in the opposite direction, which is why the pipeline uses both formats and
  neither is a mistake. Close the section at the thesis picture again: everything on this
  slide is the tall "paid once" bars; the next section is about making the arrow between
  those bars and the browser — the network — almost free.
