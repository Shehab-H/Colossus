# Prompt for Claude design — Act I: The Problem (slides 1–6)

Paste everything below the line into Claude design. The DESIGN SYSTEM block travels with every
batch; the slide specs are Act I only. If generating slide-by-slide, always include the DESIGN
SYSTEM block plus the one slide spec.

---

Create slides 1–6 of a technical engineering talk. This is a university-style tech session for
junior developers, NOT a product pitch — no marketing language, no superlatives, no stock photos,
no decorative icons that carry no information. Every element on a slide must earn its place.

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
- Diagrams: flat vector style, thin 1–2px strokes, no drop shadows, no 3D, no gradients except a
  single subtle one allowed on the title slide. Label every arrow. Diagrams read left → right.
- Footer on every slide except the title: slide number, small, low-contrast.
- Tone of all copy: precise, calm, a little dry. It should read like a good lecture handout.

## SLIDE 1 — Title

ON SLIDE
- Title: **Rendering billions of rows, honestly, in a browser**
- Subtitle: A precompute-everything engine for full-fidelity interactive visualization
- Footer line: presenter name · team · date (placeholder text)

VISUAL
- Full-bleed background: a placeholder frame marked "HERO SCREENSHOT — dense global map view"
  (we will swap in a real screenshot), dimmed to ~40% so the title carries.
- Title bottom-left, large. Nothing else.

SPEAKER NOTES
- Opener: "Every dot you'll see tonight is a real database row. That's the whole talk."

## SLIDE 2 — The ask

ON SLIDE
- Header: **The problem**
- A database table with **billions of raw rows** — locations, dimensions, metrics.
- The ask: *see all of it* in a browser. Pan, zoom, filter — aggregates recompute live.
- The constraint: **no lying.** No sampled stand-ins, no invented "average" dots.

VISUAL
- Left: stylized database table icon labeled `10⁹ rows` (monospace). Right: a browser window
  containing a map. Between them one thick arrow carrying a large "?".

SPEAKER NOTES
- Sell the honesty constraint hard: most tools solve this problem by quietly changing what you
  are looking at. We refuse that up front — it is the design constraint everything else follows
  from. "Aggregates recompute live" means: drag a date range and every polygon's average
  recomputes — that one sentence is half the engineering in this talk.

## SLIDE 3 — Why it's hard: the four walls

ON SLIDE
- Header: **Four walls between the data and the screen**
- **Network** — `10⁹ rows × 20 B` ≈ tens of GB. Can't ship it.
- **Memory** — a browser tab gets ~2–4 GB; JS objects cost ~10× the raw data.
- **CPU** — one main thread; per-row work at this scale = seconds of frozen page.
- **GPU** — *can* draw tens of millions of shapes at 60 fps — but only if fed raw binary
  buffers in exactly the layout its shaders read.

VISUAL
- Four tall vertical panels in a row, like walls, each with the bolded word + its one line.
  First three walls solid; the GPU wall drawn with a door standing ajar, lit with the red
  accent — it is the one that lets us through.

SPEAKER NOTES
- Punchline: three walls say "no". The fourth says "yes — IF you speak my language." The entire
  architecture of this system is about arriving at the GPU wall already speaking its language.
  The next stretch of the talk is the grammar of that language.

## SLIDE 4 — How existing tools cope

ON SLIDE
- Header: **Everyone gives up one of: fidelity · interactivity · scale**
- Table, 6 rows × 3 columns (Approach / Examples / What breaks):
  1. Aggregate first, render buckets — Tableau, Power BI — you see buckets, not data; every
     drill-down is a round trip.
  2. Server renders images — datashader, tile-image servers — pixels only: no per-feature
     identity, no client-side filter.
  3. Cartographic vector tiles — Mapbox GL + tippecanoe — built for maps: simplifies geometry
     and **drops features** at low zoom.
  4. Ship everything to the client — deck.gl / kepler.gl on raw files — dies past a few
     million rows.
  5. Query engine in the browser — DuckDB-WASM alone — must download the data first; every pan
     is a query; no render path.
  6. Live SQL per interaction — dashboards on a warehouse — latency and cost scale with
     users × clicks.

VISUAL
- The table is the slide. Add a small triangle glyph per row (corners labeled F / I / S for
  fidelity, interactivity, scale) with the sacrificed corner dimmed/struck in orange.

SPEAKER NOTES
- Be fair: we USE deck.gl and DuckDB — they are excellent, and we'll credit them again later.
  The claim is not that these tools are bad; it's that nobody arranges the work in the right
  place. That arrangement is the actual invention here.

## SLIDE 5 — Our answer in one picture

ON SLIDE
- Header: **Move every cost to where it's paid once, offline**
- Pipeline (monospace, one line): `any database → offline build (once) → immutable static
  files → static serve → zero-copy decode → GPU`
- Three commitments:
  1. **Full fidelity** — every rendered feature is a real source row.
  2. **All expensive work offline** — extract, index, triangulate, pre-aggregate, compress.
  3. **Interaction touches state, not data** — the source DB is queried exactly once, ever.

VISUAL
- The pipeline as 6 stage boxes left → right with a COST BAR under each stage: tall orange bars
  under the offline stages (build), near-zero red slivers under serve / decode / interact.
  This is the thesis picture of the whole talk and it returns in the final recap — make it the
  most polished diagram in the deck. Labels on the bars: "paid once" (left group), "paid per
  interaction" (right group).

SPEAKER NOTES
- "Static files" is a feature, not a limitation: no app server in the render path, trivially
  cacheable, versioned; rollback = flip a pointer. Later in the talk we'll price interactions in
  actual bytes: a filter costs two uniforms, a recolor costs a ~1 KB texture, an aggregate
  recompute costs a small ranged fetch — never a database query. Keep this slide in mind; every
  act ends by pointing at which bar it just shrank.

## SLIDE 6 — Five terms, used precisely

ON SLIDE
- Header: **Five terms, used precisely** (all industry-standard)
- **Feature** — one geometry on screen (a point, a polygon). May be backed by many rows.
- **Fact** — one source row belonging to a feature (data-warehouse sense — geometry repeats).
- **Dimension / measure** — a column you filter & group by / a column you aggregate.
- **Tile** — one file: all features for one node `z/x/y` of the spatial pyramid.
- **Partial aggregate** — an intermediate result (`sum`, `count`, `min`, `max`, weighted sum)
  that can be combined later without the raw values.

VISUAL
- Glossary card layout, 5 rows, generous spacing. Tiny inline pictograms drawn in the flat
  line style: a polygon outline (feature); the same polygon with stacked rows beneath it
  (facts); a funnel + a Σ pair (dimension/measure); a quadtree grid square (tile); Σ with a
  merge arrow (partial aggregate).

SPEAKER NOTES
- Anchor feature-vs-fact with one concrete image: "one polygon on the map, measured every month
  by four vendors — one feature, dozens of facts." The live-aggregates part of the talk rides
  entirely on that distinction, so land it now. All five terms are standard industry vocabulary —
  nothing project-internal to memorize.
