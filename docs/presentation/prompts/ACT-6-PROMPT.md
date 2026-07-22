# Prompt for Claude design — Act VI (internal name only): Trust & close, slides 31–33

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as the previous
batches — include it with every paste. Numbering continues from the aggregates batch and
already accounts for the three insert slides (slides 31–33; 33 closes the deck).

Numbers marked PLACEHOLDER must be rendered exactly as given but will be re-measured on the
final builds before talk day.

---

Create slides 31–33 of the same technical engineering talk (Vodafone-branded lecture deck,
continuing from slides 1–30). This is the closing section: how a system implemented in two
languages stays correct, the one verifier that guards fidelity end to end — including the bug
its first version failed to catch — and a recap that returns to the opening thesis picture.
Junior-dev audience — the correctness lessons here are the most transferable content in the
talk; land them plainly.

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

## SLIDE 31 — Two languages, one truth

ON SLIDE
- Header: **Every shared contract is pinned by a fixture both languages must pass**
- The build is C#; the client is TypeScript. Every contract implemented twice is pinned by a
  **shared fixture file** that both test suites must pass: tile math · the aggregate grammar ·
  the sidecar byte layout · the geometry codecs.
- Change a contract → regenerate the fixture → **both** languages must agree, or CI fails.
  Drift cannot ship silently.
- Every physical re-encoding (rows → planes, files → archive, gzip → zstd) must produce
  **byte-identical** aggregation results on those fixtures. Formats changed repeatedly;
  semantics never did.

VISUAL
- Center: one fixture-file card (monospace `{ cases… }`). Two arrows out: left into a box
  labeled `C# test suite (build)`, right into `TypeScript test suite (client)`. Each box emits
  an identical result strip into a shared `==` gate feeding a CI checkmark. Under the card,
  four small chips: `tile math` · `grammar` · `sidecar bytes` · `geometry codecs`.

SPEAKER NOTES
- The discipline worth teaching: the fixture is the specification — human-readable cases,
  machine-enforced in two languages. When a format migration lands, the fixture commit comes
  first and the two implementations follow it. This is why the aggregates section could claim
  "formats changed repeatedly, semantics never did" with a straight face: it isn't a
  recollection, it's a CI gate that fails the build the moment the two sides disagree — the
  cheapest possible insurance for a dual-language system, and the first thing to copy from
  this project into any other.

## SLIDE 32 — The verifier, and the tautology it had to escape

ON SLIDE
- Header: **`Σ count partials = source rows` — witnessed from outside**
- Every build runs a verifier: leaf counts, summed through the real archive and real byte
  ranges, must equal the row count **the source reported**. One equation — it catches a fact
  dropped or duplicated anywhere in the pipeline.
- The war story: the first version compared the manifest's total to the sum of the leaves —
  but the manifest total was *defined* as that sum. **A tautology. It passed for months while
  tiles double-counted rows on their seams.**
- The fix: the witness must come from **outside the build** — the source's own row count and
  the staged extract.
- Plus byte-level spot checks: a ranged, decompressed block asserted equal to an independent
  decode of the whole file.

VISUAL
- The equation across the top, large monospace: `Σ count partials == source rows ✓`. Below,
  two panels. Left (orange): a circular arrow loop `manifest total ⟳ leaf sum`, labeled
  "defined as each other — always passes", with a small annotation `…for months`. Right
  (red): a straight left-to-right chain `source row count → staged extract → leaves → Σ`,
  ending in the checkmark.

SPEAKER NOTES
- The lesson for juniors, stated plainly: a check is only as strong as the **independence of
  its witness** — a verifier that derives both sides from the same code path proves only that
  the code equals itself. The seam double-counting bug promised back in the build section was
  real, and it survived precisely as long as the witness was circular; the day the comparison
  was re-anchored to the source's own count, it surfaced immediately. Since then the equation
  has run on every build, read through the real archive and real ranges — it is the sentence
  that lets the title slide say "honestly".

## SLIDE 33 — Where every cost went

ON SLIDE
- Header: **Where every cost went**
- Table (Cost / Paid / How):
  1. Query the source — once, offline — adapter extraction
  2. Spatial indexing, triangulation — offline — pyramid + precomputed triangles
  3. Parse/decode on the client — ~zero — zero-copy tile contract
  4. A filter — ~zero bytes — attributes + two uniforms
  5. A recolor — ~`1 KB` — lookup-table texture
  6. An aggregate recompute — a small ranged fetch + a worker pass — planes, prefix sums,
     cell-row slices
  7. An over-budget view — one small response — server aggregation over build artifacts
- Footnote, small, orange: still open — a ~`230 ms` render stutter when a zoom admits a
  screenful of new tiles (PLACEHOLDER — measured baseline).
- Closing line, large, alone at the bottom: *Fidelity, interactivity, and scale stop being a
  pick-two the moment you're willing to do all the work before anyone asks for it.*

VISUAL
- Top: the thesis pipeline picture from the opening returns — same six stages, same cost
  bars — with the right-hand slivers now annotated with the measured figures from the table
  (`2 uniforms`, `~1 KB`, `ranged fetch`). The table beneath it. The closing line is the
  final element on the final slide — set it large, near-black, nothing after it. Footer note:
  Q&A · backup slides available.

SPEAKER NOTES
- Walk the table one row at a time, pointing back at the section that earned it — each row is
  one of the thesis picture's bars shrinking. Then the honest frontier, said out loud rather
  than hidden: the remaining measured problem is a ~230 ms stutter when a zoom swaps a
  screenful of tiles into the renderer — the layer-admission cost. The candidate fixes (GPU
  buffer ownership, arena allocation with multi-draw) are specced with entry criteria and
  it's the next measurement-gated project; admitting it closes the trust argument better than
  any benchmark could. Then the closing line, verbatim, and stop talking. Q&A. Backup slides
  on hand: plane/CSR byte layout, aggregate finalization semantics, archive directory format,
  measured benchmark tables.
