# Prompt for Claude design — Act IV (internal name only): Shrinking the wire, slides 18–21

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as the previous
batches — include it with every paste. Slides continue numbering (18–21).

Numbers marked PLACEHOLDER must be rendered exactly as given but will be re-measured on the
final builds before talk day.

---

Create slides 18–21 of the same technical engineering talk (Vodafone-branded lecture deck,
continuing from slides 1–17). The build section ended with tiles as static files; this section
is about the network between those files and the browser — how tile traffic shrank by more than
an order of magnitude with **zero loss**: not by compressing harder, but by not storing what is
derivable and not sending what no interaction has asked for. Junior-dev audience — define every
term the first time it matters.

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

## SLIDE 18 — The problem: lossless has a floor, and we hit it

ON SLIDE
- Header: **You can't compress your way out**
- House rule: **lossless only.** Every byte must reconstruct bit-for-bit — no quantized
  coordinates, no rounded measures.
- Two stubborn facts about a tile's bytes:
  - real-valued `f32` measure columns are near-random bits — general-purpose compression
    barely dents them
  - geometry was **69–99%** of a polygon tile — and almost all of it is *derivable*
- So the wins are not better compression. They are: **don't store the derivable** (this slide's
  neighbor), and **don't send the un-asked-for** (the one after).

VISUAL
- A tile drawn as a stacked byte bar: a huge segment labeled `geometry` (orange, annotated
  "derivable — why is it stored?"), smaller segments `measures (f32)` (annotated "incompressible
  by nature"), `dimension codes`. Next to it a small pressed-flat bar labeled "gzip: −x%" with a
  shrug annotation — compression alone doesn't move the picture.

SPEAKER NOTES
- Recall the network wall from the four-walls slide — this section is where it actually falls.
  Explain the lossless floor honestly: compressed size is bounded by real information content;
  measured f32 values are nearly all information, so the only compressible thing in a tile is
  structure. The insight that unlocked everything: geometry LOOKS like information but mostly
  isn't — a grid cell's corners are implied by its grid position; a real ring's coordinates are
  strongly correlated with their neighbors. Derivable structure can be reconstructed instead of
  shipped.

## SLIDE 19 — Deep dive: geometry that isn't stored, only described

ON SLIDE
- Header: **Two codecs, chosen per tile, verified bit-for-bit**
- A tile's geometry column is replaced by one compact self-describing payload. The client
  rebuilds the exact position, offset, and triangle buffers from it.
- **rect** — when every feature is an axis-aligned rectangle (grid-cell datasets — the bulk):
  a row is just **four `u16` indices** into per-tile sorted tables of corner coordinates.
  Positions, triangles, offsets: all derived.
- **delta** — any real ring: split x from y, reinterpret each `f32`'s bits as an integer, store
  the **difference from the previous vertex** (tiny — neighbors are close), zigzag-encode,
  regroup bytes by significance. Triangle indices stored row-local at minimal width.
- The encoder *proves* rect reconstructs bit-for-bit before choosing it — a mis-choice cannot
  ship.

VISUAL
- Two panels. Left "rect": a grid-cell rectangle whose four corners point via thin red arrows
  into two short sorted tables (`x corners`, `y corners`); under it, monospace:
  `1 row = 4 × u16 = 8 B`. Right "delta": a jagged ring with arrows between consecutive
  vertices labeled `+3, −1, +2…` (tiny numbers), and a four-step mini-chain in monospace:
  `split x|y → bits → Δ → zigzag → transpose`. Bottom strip across both: `decode → identical
  buffers` with a checkmark.

SPEAKER NOTES
- Why delta works is the Hilbert payoff planted in the build section: extraction ordered rows
  along a space-filling curve, so consecutive features are spatial neighbors and consecutive
  coordinates differ in their low bits only — deltas of reinterpreted bits are tiny integers
  full of leading zeros, which THEN compress superbly. Lossless floats via bit reinterpretation
  is the trick to teach here: we never do float arithmetic on coordinates, we treat the 32 bits
  as an opaque integer, so reconstruction is exact by construction. The triangle side is free:
  the ear-clipper is deterministic, so per-part triangle counts are derivable and no triangle
  offsets are stored at all.

## SLIDE 20 — Don't send what no one asked for

ON SLIDE
- Header: **A first paint fetches geometry + one column**
- All tiles of a build version live in **one archive file**; every tile *column* is its own
  independently compressed block; a directory maps `tile → column → byte range`.
- Block order is the design: `geometry → default color column → filter columns → everything
  else`. So the default first paint is **one contiguous byte-range request per tile**.
- Every other column stays on the server until an interaction actually reads it — switch the
  color encoding, click to inspect → fetch that column, for resident tiles, once.
- A whole-tile read is still one range: the same blocks, end to end.

VISUAL
- A long horizontal byte strip = the archive. Zoom into one tile's span showing its ordered
  blocks: `geom` `color` `filter×2` then several grey `other` blocks. A red bracket over the
  first four labeled "first paint: 1 range". A second, lighter bracket over the whole span
  labeled "whole tile: still 1 range". Below, a small sequence: color-switch icon → arrow
  fetching one grey block only.

SPEAKER NOTES
- The principle generalizes the whole talk's law from compute to bytes: interactions may only
  pay for what they touch. Nothing here is heuristic — the column order comes from the view's
  declared channel roles, and the client knows from the directory exactly which ranges to ask
  for; no server logic, still just static file + HTTP range requests. Mention the operational
  bonus: one archive instead of thousands of tiny tile files makes deploys and CDN caching
  dramatically simpler, and the cache identity stays `(version, tile)` so a colour switch can
  never invalidate anything already resident.

## SLIDE 21 — The codec story, and what it all measured

ON SLIDE
- Header: **Paid for a decoder only when the numbers said so**
- v1: gzip per block — the browser decompresses natively (`DecompressionStream`), zero client
  dependencies.
- Measurement: zstd at max level beat gzip by **~28%** on our blocks — and small per-column
  blocks compress poorly alone, so a **shared dictionary** is trained per build and shipped
  once (~a few hundred KB of WASM decoder + dictionary, loaded once per worker).
- Why compression lives *inside* the archive: HTTP `Content-Encoding` does not compose with
  range requests — a ranged slice of a compressed whole is garbage; a ranged slice of
  independently compressed blocks is a valid block.
- Measured first paint vs. shipping the old whole tiles (PLACEHOLDER, re-measured on final
  builds): **~69×** / **~27×** / **~16×** smaller across the three reference views.

VISUAL
- Top: a two-step timeline — "gzip (free, native)" → "zstd + trained dictionary (measured
  ~28% win)" with the arrow labeled "only after measuring". Bottom: three big red ratio
  figures `69×  27×  16×` as the section's closing numbers, each above a small faded bar-pair
  showing before/after byte bars. Keep the ratios the visual hero of the slide.

SPEAKER NOTES
- The engineering lesson to say out loud: the first version used the codec the browser gives
  you for free, and the WASM decoder was added only when a measurement put ~28% on the table —
  dependency cost is real and must be bought with numbers, not taste. Explain the dictionary in
  one line: small blocks can't learn their own statistics, so we train the statistics once per
  build and share them across every block. Close the section against the thesis picture: the
  network arrow is now tens of times thinner, still bit-for-bit lossless. One honest caveat for
  Q&A: these ratios are per-view measurements, not universals — point-heavy views sit at the
  low end, grid-polygon views at the high end, which is exactly what the geometry codec
  predicts.
