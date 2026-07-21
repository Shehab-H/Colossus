# Prompt for Claude design — Act II (internal name only): Bytes to pixels, slides 7–12

Paste everything below the line into Claude design. Same DESIGN SYSTEM block as batch 1 — include
it with every paste. Slides continue numbering from the previous batch (7–12).

---

Create slides 7–12 of the same technical engineering talk (Vodafone-branded lecture deck,
continuing from slides 1–6). This section explains how bytes become pixels: what a GPU actually needs, why common
data formats can't feed it, and the render path that fixes that. Junior-dev audience — define
every term the first time it matters.

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

## SLIDE 7 — GPU 101, scoped to exactly what we use

ON SLIDE
- Header: **The GPU's language — five words**
- **Vertex** — one corner of a drawn primitive. A point feature = 1 vertex; a polygon = its
  triangle corners.
- **Shader** — a small program run massively in parallel: the *vertex shader* per vertex, the
  *fragment shader* per pixel.
- **Attribute** — per-vertex input data (position, a feature's value): big typed arrays,
  uploaded ahead of time.
- **Uniform** — one value shared by every vertex in a draw call (e.g. the active filter range).
- **Texture** — a small image a shader can read as a lookup table — not just for pictures.
- Bottom rule, emphasized: **per-feature data crosses to the GPU once; per-interaction data is
  uniforms and textures — a handful of bytes, not megabytes.**

VISUAL
- Right half: a minimal pipeline diagram — a box of `attributes` (drawn as parallel typed-array
  strips) flowing into `vertex shader` → `fragment shader` → screen pixels; `uniforms` and
  `textures` feeding into the shader boxes from above as thin side-arrows (red). Left half: the
  five glossary rows.

SPEAKER NOTES
- This resolves slide 3's cliffhanger: the GPU wall has a door, and this is the language spoken
  at the door. Spend time here — everything for the next twenty minutes is stated in these five
  words. The bottom rule is the design law of the whole client: if an interaction ever touches
  an attribute, we lost; interactions may only touch uniforms and textures.

## SLIDE 8 — The problem: data formats are hostile to GPUs

ON SLIDE
- Header: **What the GPU wants vs. what formats deliver**
- Wants: contiguous typed arrays — `Float32Array` of positions, `Uint32Array` of triangle
  indices — handed over as-is.
- Delivers:
  - JSON / GeoJSON — parse every character, build an object per feature.
  - Parquet — superb on disk, but decompress + decode per column block.
  - Vector tiles (protobuf) — decode per value, and still no triangles.
- The standard pipeline: download → parse → objects → *convert again* into typed arrays.
  At millions of features, **the conversion is the bottleneck, not the network.**

VISUAL
- Left: three format cards stacked (JSON, Parquet, protobuf tile), each with a small orange tag
  stating its per-value cost ("parse chars", "decode blocks", "decode varints"). Right: the
  clean typed-array strips from slide 7 (red). Between them a tangled arrow labeled
  "parse → objects → convert" with an orange cost burst.

SPEAKER NOTES
- Be concrete about "an object per feature": a JS object costs roughly 10× the raw bytes and
  scatters them across the heap — the exact opposite of the contiguous buffer an attribute needs.
  Every mainstream stack pays this conversion tax on the main thread or a worker at load time.
  Question to plant: what if the file on disk were *already* the typed array?

## SLIDE 9 — Our tile format: the file IS the buffer

ON SLIDE
- Header: **Arrow IPC: reading a tile is a memcpy**
- **Apache Arrow** — a standardized *in-memory* columnar layout: each column one contiguous
  typed buffer. **IPC** — that exact memory written to disk with a small framing header.
- So: the column buffer in the file *is* the `Float32Array` the renderer uploads. Decode step:
  none.
- We harden this into a zero-copy contract per tile:
  - a single record batch (one contiguous chunk per column)
  - no nulls anywhere — missing numerics are `NaN`, written at build time
  - numeric columns stored as `f32` — the stored buffer is the render buffer
  - categorical columns dictionary-encoded in one canonical order — the codes in the file are
    the codes the client already knows
- Decoding runs in a small worker pool; buffers transfer to the main thread zero-copy.

VISUAL
- A tile file drawn as a horizontal byte strip segmented into labeled column buffers
  (`positions`, `value`, `category codes`, `triangle indices`), with straight red arrows from
  each segment directly to GPU attribute slots on the right. Caption under the arrows,
  monospace: `decode step: none`. The four contract bullets as a checklist beside it.

SPEAKER NOTES
- Why not Parquet for tiles? Parquet is encoded and compressed per value block — perfect when
  the job is *scanning* (we still use it at build time and for the server-side fallback), wrong
  when the job is "read once, hand to the GPU." Each contract bullet kills one class of
  client-side work: single batch kills reassembly, no-nulls kills the null bitmap branch, f32
  kills casting, canonical dictionary order kills code remapping. None of this is Arrow's
  default behavior — it's a discipline the build enforces so the client can be dumb and fast.

## SLIDE 10 — Deep dive: polygons are triangulated before they're served

ON SLIDE
- Header: **GPUs draw triangles. We triangulate offline.**
- Turning a polygon (holes and all) into triangles = **tessellation** (ear clipping) —
  real CPU work per ring, classically done in the browser at load time.
- Everyone else: tessellate on the client (main-thread stalls that scale with feature count),
  or pre-render pixels server-side (feature identity gone).
- Ours: tessellate **during the offline build**; store the triangle indices as a tile column,
  already offset to be tile-global. The renderer takes the positions buffer + the index buffer
  as-is. Geometry math in the browser: **zero**.

VISUAL
- Left: one concave polygon with an ear being clipped (dashed triangle, red), then the fully
  triangulated result. Right: two horizontal timeline bars comparing "load time elsewhere:
  download → tessellate (long orange segment) → upload" vs "ours: download → upload" — the
  tessellate segment moved to a separate faint bar above labeled "offline build, paid once".

SPEAKER NOTES
- Define ear clipping in one breath: repeatedly slice off a triangle whose corner points
  "outward" until the ring is gone — works for convex and concave rings, either winding.
  The subtle half of the win: indices are rebased to be tile-global *at write time* (the writer
  keeps a running vertex base), so the client doesn't even do one add per index — it takes one
  view over the whole buffer. This single decision — moving tessellation offline — was the
  largest render-path win in the project's history: render stutter used to scale with polygon
  count; now the browser never runs geometry code at all.

## SLIDE 11 — Recoloring without touching data

ON SLIDE
- Header: **A recolor moves a 4 KB texture, not megabytes**
- Naive: "color by X" → compute an RGB per feature → re-upload an attribute. Megabytes per
  click.
- Ours: the color scale is sampled once into a small **RGBA lookup-table texture** (1024 texels
  numeric; one texel per category + one `unknown` texel categorical). Each feature carries one
  float attribute — its value — uploaded once.
- Vertex shader: value → normalize by domain (linear or log — a uniform decides) → texture
  coordinate → color.
- A recolor = swap the texture + a few uniforms. Per-feature traffic: **zero**.

VISUAL
- Flow diagram: a feature's `value` (monospace float) → a normalize box (labeled
  `domain, transform` with a uniform side-arrow) → a horizontal color-ramp strip (the texture,
  drawn as a gradient bar with texel tick marks) → a colored polygon. Below, a comparison chip:
  "naive: N features × 4 B re-upload (orange)" vs "ours: 4 KB texture + 3 uniforms (red)".

SPEAKER NOTES
- The trust detail worth saying aloud: the CPU color-scale code stays the single authority —
  every texel is that function evaluated at a real value, and tests assert the texture equals
  the CPU scale, so the GPU can never disagree with the legend. Log scales don't get a shader
  reimplementation either: the same uniform-driven normalize, verified against the CPU.

## SLIDE 12 — Filtering without refetching

ON SLIDE
- Header: **A filter change is two uniforms**
- Every filterable column rides in the tile as one float **attribute** per feature (up to four
  slots): category → its dictionary code, date → a day number.
- Toggling a filter updates two uniforms — `filterRange`, `filterEnabled` — and the vertex
  shader discards non-matching features. No fetch, no decode, no re-upload.
- Tiles stay resident on the GPU across interactions. Cache identity is
  `(version, tileKey)` — color and filter state are GPU state, never part of data identity.

VISUAL
- Split panel. Left, "typical", a long orange chain: `filter → query → transfer → parse →
  upload → draw`. Right, "ours", a short red chain: `filter → 2 uniforms → draw`. Make the
  length difference the whole point of the graphic. Small footnote diagram: four labeled filter
  slots feeding one range-check box in the vertex shader.

SPEAKER NOTES
- Ranges express everything: a category equality is the degenerate range [code, code]; an
  open-ended date side uses a ± sentinel; "no filter" is a wide-open range, and a flag skips
  the shader cost entirely when nothing filters. The residency point closes the loop on slide
  7's law: since identity never includes color or filter, no interaction can ever invalidate a
  resident tile — which is exactly why interactions cost microseconds. End of this stretch of
  the talk: look back at slide 5's cost bars — decode and interact are now near-zero by
  construction. Next problem: how billions of rows become these tiles in the first place.
