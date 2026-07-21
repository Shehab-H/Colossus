# Batch 1 — Act I: The Problem (slides 1–6)

Feed Claude design ONE slide block at a time. Per slide: `ON SLIDE` = exact copy to render,
`VISUAL` = the layout/graphic to build, `NOTES` = speaker notes (put in presenter notes, not on
the slide). House rules: dark theme, one idea per slide, on-slide text stays terse — the notes
carry the narration.

---

## Slide 1 — Title

ON SLIDE
- Title: **Rendering billions of rows, honestly, in a browser**
- Subtitle: A precompute-everything engine for full-fidelity interactive visualization
- Footer: presenter name · team · date

VISUAL
- Full-bleed hero screenshot of the dense global view as background (dimmed ~40%), title over it.

NOTES
- One-liner opener: "Every dot you'll see tonight is a real database row. That's the whole talk."

---

## Slide 2 — The ask

ON SLIDE
- Header: **The problem**
- A database table with **billions of raw rows** — locations, dimensions, metrics.
- The ask: *see all of it* in a browser. Pan, zoom, filter — aggregates recompute live.
- The constraint: **no lying.** No sampled stand-ins, no invented "average" dots.

VISUAL
- Left: a stylized DB table icon labeled "10⁹ rows". Right: a browser window with a map.
  A thick arrow between them with a "?" on it.

NOTES
- Sell the honesty constraint hard: most tools solve this by quietly changing what you're
  looking at. We refuse that up front — it's the design constraint everything follows from.

---

## Slide 3 — Why it's hard: the four walls

ON SLIDE
- Header: **Four walls between the data and the screen**
- **Network** — 10⁹ rows × 20 B ≈ tens of GB. Can't ship it.
- **Memory** — a browser tab gets ~2–4 GB; JS objects cost ~10× the raw data.
- **CPU** — one main thread; per-row work at this scale = seconds of frozen page.
- **GPU** — *can* draw tens of millions of shapes at 60 fps — but only fed raw binary buffers
  in exactly the layout its shaders read.

VISUAL
- Four vertical wall panels in a row, each with icon + the bolded word + its one line.
  The GPU wall drawn slightly open (a door) — it's the one that lets us through.

NOTES
- Punchline: three walls say "no". The fourth says "yes, IF you speak my language." The entire
  architecture is about arriving at the GPU wall already speaking its language.

---

## Slide 4 — How existing tools cope

ON SLIDE
- Header: **Everyone gives up one of: fidelity · interactivity · scale**
- Table (6 rows, 3 columns: Approach / Examples / What breaks):
  1. Aggregate first, render buckets — Tableau, Power BI — you see buckets, not data; every
     drill-down is a round trip.
  2. Server renders images — datashader, tile-image servers — pixels only: no per-feature
     identity, no client-side filter.
  3. Cartographic vector tiles — Mapbox + tippecanoe — built for maps: simplifies and **drops
     features** at low zoom.
  4. Ship everything to the client — deck.gl / kepler.gl on raw files — dies past a few million
     rows.
  5. Query engine in the browser — DuckDB-WASM alone — must download data first; every pan is a
     query; no render path.
  6. Live SQL per interaction — dashboards on a warehouse — latency + cost scale with
     users × clicks.

VISUAL
- The table, with a small triangle icon per row showing which corner (fidelity / interactivity /
  scale) that approach sacrifices.

NOTES
- Be fair: we *use* deck.gl and DuckDB — they're excellent. The claim is nobody arranges the
  work in the right place. That's the gap.

---

## Slide 5 — Our answer in one picture

ON SLIDE
- Header: **Move every cost to where it's paid once, offline**
- Pipeline: `any database → offline build (once) → immutable static files → static serve →
  zero-copy decode → GPU`
- Three commitments:
  1. **Full fidelity** — every rendered feature is a real source row.
  2. **All expensive work offline** — extract, index, triangulate, pre-aggregate, compress.
  3. **Interaction touches state, not data** — the source DB is queried exactly once, ever.

VISUAL
- The pipeline as 6 stage-boxes left→right, with a COST BAR under each: tall red bars under the
  offline stages, near-zero green slivers under serve/decode/interact. This is the thesis
  picture — make it the most polished diagram in the deck (it returns in the recap).

NOTES
- "Static files" is a feature, not a limitation: no app server in the render path, trivially
  cacheable, versioned, rollback = flip a pointer. Interactions later in the talk will cost
  bytes (uniforms) or tiny ranged fetches — never a database query.

---

## Slide 6 — Vocabulary (5 terms, standard meanings)

ON SLIDE
- Header: **Five terms, used precisely** (all industry-standard)
- **Feature** — one geometry on screen (point, polygon). May be backed by many rows.
- **Fact** — one source row belonging to a feature (warehouse sense — geometry repeats).
- **Dimension / measure** — column you filter & group by / column you aggregate.
- **Tile** — one file: all features for one node `z/x/y` of the spatial pyramid.
- **Partial aggregate** — intermediate result (`sum`, `count`, `min`, `max`, weighted sum) that
  combines later without the raw values.

VISUAL
- Glossary card layout, 5 rows. Tiny inline pictograms: polygon icon (feature), stacked rows
  under it (facts), grid (tile), Σ (partial aggregate).

NOTES
- Anchor feature-vs-fact with one concrete image: "one polygon on the map, measured every month
  by four vendors — one feature, dozens of facts." Everything in Act IV rides on that
  distinction.

---

Next batches: 02 render path (formats→GPU, Arrow IPC, GPU 101, triangulation, recolor, filter),
03 offline build, 04 aggregates (includes the dedicated ".pack archive" slide and the
"fetch only what you color & filter by" slide), 05 trust + recap.
