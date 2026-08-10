# Prompt for Claude design — three insert slides (dictionary encoding, Hilbert sort, marks & facts)

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as every batch.
These are three standalone slides to INSERT into the existing deck at the stated positions —
regenerate nothing else. If the tool renumbers footers automatically, that is fine.

- Slide A inserts **after slide 9** (the Arrow tile-format slide).
- Slide B inserts **after slide 13** (the adapter/canonical-schema slide).
- Slide C inserts **after slide 21** (it opens the aggregates section that follows).

When later batches are generated, their slide numbers shift accordingly.

---

Create three insert slides for the same technical engineering talk (Vodafone-branded lecture
deck). Junior-dev audience — define every term the first time it matters.

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

## SLIDE A (after slide 9) — Strings become integers, once

ON SLIDE
- Header: **Dictionary encoding: one canonical order, zero remapping**
- **Dictionary encoding** — store each distinct string once, in a small table; every row
  carries a small integer code instead.
- We fix **one canonical order** per column at build time and record it in the manifest.
- That same code then means the same category *everywhere* — nothing ever remaps, nothing
  ever compares strings at runtime:
  - tile column → the code, as the feature's attribute
  - GPU filter → equality is the range `[code, code]`
  - color texture → texel index = code
  - aggregate result → the winning category returns as a `u16` code
  - UI dropdown → option list index = code

VISUAL
- Center: one large monospace integer `3`. Five thin red arrows radiate to five mini-panels:
  a tile column strip with one cell highlighted; a filter chip reading `[3, 3]`; a color-ramp
  strip with texel 3 highlighted; a chip reading `argmax → 3`; a dropdown list with the 4th
  row highlighted. Caption beneath, monospace: `decided once, offline — never remapped`.

SPEAKER NOTES
- The byte win is the obvious half — a repeated string per row is deadweight at this scale.
  The deep win is the *contract*: because the order is canonical and baked, the client never
  builds a mapping table at any boundary; the code flows from the database to the pixel
  unchanged. Dirty data stays honest too: a value outside the canonical list gets a sentinel
  code that no filter matches and the color scale renders as its explicit "unknown" color —
  bad data is visible, never silently blended. This slide is also homework for later: in the
  aggregates section these codes stop being labels and become *coordinates*.

## SLIDE B (after slide 13) — One sort, three payoffs

ON SLIDE
- Header: **The Hilbert sort: neighbors in space, neighbors in the file**
- Extraction orders every row along a **Hilbert curve** — a space-filling curve: a single path
  that visits every cell of a grid, keeping nearby cells at nearby positions along the path.
  It turns 2-D locality into 1-D order.
- One `ORDER BY`, three separate payoffs:
  1. **build** — per-tile scans read contiguous runs of the staged file, not the whole file
  2. **tiles** — a tile's rows arrive together and stay spatially coherent inside the tile
  3. **compression** — consecutive rows are spatial neighbors, so coordinate differences are
     tiny (this one pays off two sections from here)

VISUAL
- Left: the classic Hilbert curve evolution — order-1, order-2, order-3 U-shapes drawn small
  to large, the order-3 curve in red. Right: a 2-D scatter of grey points with the curve
  threading through them, then an arrow "unroll" to a 1-D file strip where clustered points
  land as contiguous colored runs. Three small payoff chips along the bottom.

SPEAKER NOTES
- Why not just sort by x then y? Because that scatters y-neighbors: two points side by side
  vertically end up far apart in the file. The curve keeps BOTH dimensions local — that's the
  whole trick, and it's why this one clause quietly improves the build, the tiles, and the
  compression at once. Practical note: the sort runs inside the source database during
  extraction (modern engines ship a Hilbert-encode function), so the build never has to hold
  the dataset in memory to sort it. Tell the audience to remember this slide when geometry
  gets delta-encoded later — it's the setup for that punchline.

## SLIDE C (after slide 21 — opens the aggregates section) — The array index is the primary key

ON SLIDE
- Header: **How features and facts are stored: position IS the key**
- A render tile stores exactly **one row per feature**; a feature's identity is its row
  position in the tile.
- All of a feature's **facts** live in a sidecar file beside the tile — parallel arrays
  indexed by that same position.
- There is no ID column on the wire. The join is array indexing: `facts of feature i` =
  `slot i`. (The old id column was deleted — the client read it nowhere.)
- Everything downstream joins positionally: fact arrays, recomputed aggregates, even
  server responses return plain columns in feature order.

VISUAL
- Top: a render-tile strip of rows `0 … n`, row `3` highlighted red. Below it: three parallel
  sidecar array strips (labeled as fact data), each with slot `3` highlighted, connected to
  the tile row by one straight vertical red line labeled `join = index — 0 bytes of keys`.
  To the side, a small struck-through chip: `id column — deleted: read nowhere`.

SPEAKER NOTES
- Juniors are taught that joins need keys. Here the build controls BOTH sides of the join, so
  it aligns them by construction and the key simply vanishes — from every tile, every fact
  file, and every response. That discipline is why a server-side aggregation response costs
  about 4 bytes per feature per aggregate: it's just columns in feature order, nothing else.
  This slide is the doorway to everything that follows: the next slides take the fact side —
  currently "parallel arrays" — and make it filterable at interactive speed, first by turning
  fact keys into coordinates, then by turning coordinates into planes you can subtract.
