# 02 — Arena allocation and multi-draw

- **Status:** Abandoned with the reverted saga
- **Estimate:** 10–15 engineering days

## Goal

Replace one deck.gl layer and buffer set per tile with a custom point/polygon rendering layer backed by
large per-attribute arena buffers. Tiles suballocate ranges on admission and return them on eviction;
visible ranges draw through `WEBGL_multi_draw` when supported and a per-range fallback otherwise.

## Feasibility proof

The tested WebGL2 device exposed `WEBGL_multi_draw`, and one call rendered two independent vertex ranges
with the expected pixels. luma.gl exposes the WebGL2 context and mutable buffers required for an allocator.

## Project integration surface

- Replace the per-tile `ScatterplotLayer`/`SolidPolygonLayer` mapping in
  [`MapViewerPage.tsx`](../../../../web/src/features/map-viewer/MapViewerPage.tsx) with custom deck.gl `Layer` implementations.
- Implement arena allocation, growth, free-list reuse, fragmentation metrics, and deterministic cleanup.
- Port the existing GPU filter and color-LUT shader behavior from
  [`gpuFilter.ts`](../../../../web/src/features/map-viewer/engine/gpuFilter.ts) and
  [`colorScaleExtension.ts`](../../../../web/src/features/map-viewer/engine/colorScaleExtension.ts).
- Redesign click mapping: picking currently resolves a tile through its unique layer ID, while one arena
  layer must map a global picked mark back to `(tileKey, localMarkIndex)`.
- Handle point and polygon draw topology, external triangle indices, coordinate systems, MapLibre overlay,
  orthographic mode, and cover-tile swaps.

## Main risks

- This replaces substantial deck.gl machinery and is the highest-risk item in the saga.
- `WEBGL_multi_draw` is not universally available; correctness cannot depend on the extension.
- Index rebasing, allocator fragmentation, and range reuse can create rare visual or picking corruption.
- Updating the arena during tile-cover transitions must be atomic from the rendered frame's perspective.

## Acceptance evidence required

- Point and polygon parity for rendering, filtering, color, opacity, and inspection.
- Multi-draw and fallback paths produce identical pixels and picks.
- Tile admission/eviction does not leak or overwrite neighboring ranges.
- A realistic many-tile trace shows layer count, allocation time, draw overhead, and worst-frame duration
  materially below the current layer-per-tile renderer.
