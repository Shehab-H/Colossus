# Phase 5 — Deferred Frontier (DO NOT BUILD)

Nothing in this file has a green light. Each item has **entry criteria** — measurable evidence that
must exist before proposing it to the owner. This file exists so the ideas aren't re-derived from
scratch later, and so Phases 1–4 aren't accidentally designed in ways that foreclose them.

---

## 5.1 GPU buffer ownership (upload once, release the heap)

**Idea:** create luma.gl `Buffer` objects per tile ourselves, upload on tile arrival, hand deck the
GPU buffer handles instead of typed arrays (deck 9 binary attributes accept luma Buffers — verify
against the installed version's `binary-attributes` docs/source before designing further). The CPU
typed arrays are then released immediately; the heap retains only the raw tile `buffer` (Phase 3)
for picking/inspect. The cache budget splits into a GPU budget (render residency) and a smaller
heap budget (raw bytes).

**Entry criteria:** heap profiles showing tile-array residency (not the raw buffers) is the
dominant memory cost after Phase 3, **or** GC pauses attributable to tile churn.

**Design constraints to respect now:** none — Phases 1–3 already centralize attribute construction
in `tileDeckData`, which is the single seam this would replace.

## 5.2 Arena allocation + multi-draw (one layer, no per-tile GPU churn)

**Idea:** replace layer-per-tile with one custom layer per view holding large per-attribute arena
buffers; tiles suballocate ranges (`bufferSubData` on arrival, free-list on evict); draw via
`WEBGL_multi_draw` or per-range draws. Kills per-tile layer setup, buffer allocation, and draw-call
overhead; zoom swaps stop touching the allocator entirely.

**Entry criteria:** profiling showing layer creation/GPU allocation as a top cost at realistic tile
counts (start worrying above ~200 resident tiles), after 5.1 is in.

**Warning:** this abandons a lot of deck.gl machinery (picking, extensions from Phases 1–2 need
porting into the custom layer's shaders). It is weeks of work. The Phase 1/2 shader logic
(filter test, LUT lookup) is deliberately simple GLSL precisely so it ports.

## 5.3 Per-vertex expansion removal (vertex pulling / data textures)

**Idea:** polygon layers still expand per-mark values to per-vertex (`filterValues`, Phase 2's
`getScaleValue`). Instead: bake a per-vertex `markId` column (Uint32/vertex, written once at bake),
keep per-mark values in a width-capped data texture, and `texelFetch` in the vertex shader:
`markId → value → LUT`. All per-vertex client-built arrays disappear; filter/color data per tile
becomes exactly one small texture per channel.

**Entry criteria:** polygon-heavy views where expansion time or memory (`vertexCount × slots × 4`
bytes) is measurably significant. Natural companion to 5.2; pointless before it unless a real view
hurts.

**Bake note:** `markId` is a mechanical writer addition (row index repeated per vertex) — if a
tile-format v3 ever happens for other reasons, add it then.

## 5.4 WebGPU

**Idea:** deck 9 on the WebGPU device: storage buffers (native vertex pulling → 5.3 for free),
compute-shader filter *compaction* (filtered marks cost zero vertices instead of shader-discard),
GPU-side domain/aggregate computation, GPU picking without readback stalls.

**Entry criteria:** deck.gl's WebGPU backend reaching parity for the layers/extensions used here
(track deck release notes past 9.3), plus a product need Safari/driver coverage doesn't veto.

## 5.5 SharedArrayBuffer

**Idea:** tiles decoded into SAB so multiple threads (main, picking worker, stats worker) read the
same bytes without transfer or clone. Requires COOP/COEP headers on the serve.

**Entry criteria:** a second consumer thread actually existing. Transfer is already zero-copy for
the single-consumer pipeline — SAB solves a problem Colossus does not have yet.

## 5.6 LOD delta encoding — rejected

Children encoded as refinements of parents so zoom-in fetches deltas only. Recorded for
completeness: the complexity is large, it couples tile decoding across levels (breaking
tile-as-unit residency, cancellation, and the cover-swap model), and Phase 4's prefetch + pack
attack the same latency for a fraction of the cost. Do not revisit without extraordinary evidence.
