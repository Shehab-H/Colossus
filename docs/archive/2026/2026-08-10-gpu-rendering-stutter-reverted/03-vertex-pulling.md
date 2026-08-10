# 03 — Vertex pulling and data textures

- **Status:** Abandoned with the reverted saga
- **Estimate:** 6–10 additional engineering days

## Goal

Stop expanding per-mark color and filter values across every polygon vertex. Vertices carry a compact mark
ID; shaders use that ID to fetch per-mark values from width-capped textures before applying the existing
filter and color-LUT logic.

## Feasibility proof

A WebGL2 vertex shader successfully used `texelFetch(markId)` against an `R32F` data texture and rendered two
marks with distinct expected values. The tested device reported 16 vertex texture units and a maximum
texture dimension of 16,384.

## Project integration surface

- Replace `expandToVertices` in [`deckData.ts`](../../../../web/src/features/map-viewer/engine/deckData.ts) and polygon expansion in
  [`gpuFilter.ts`](../../../../web/src/features/map-viewer/engine/gpuFilter.ts).
- Add a stable per-vertex mark ID. Prefer baking it into the geometry payload; decide whether that is a new
  tile format or a backward-compatible geometry block, and preserve older-bake decoding.
- Extend the C# tile writer/schema and TypeScript geometry decoder together, with cross-language fixtures.
- Implement 2D texture packing for counts beyond one texture row, per-tile/per-arena texture allocation,
  folded-context updates, and resource cleanup.
- Port color/filter shader access to pulled values while preserving flat per-mark polygon behavior.

## Main risks

- Maximum texture dimensions and formats vary by GPU; paging and capability checks are mandatory.
- Float/integer format choices can change categorical codes or missing-value semantics.
- A bake-format change touches server writer, manifests, packed tiles, worker decode, fixtures, and backward
  compatibility.
- Implementing this without the arena layer may duplicate custom shader work later.

## Acceptance evidence required

- Pixel/filter/picking parity across point and polygon views, including missing and categorical values.
- Per-vertex color/filter arrays disappear from CPU and GPU accounting.
- Texture updates remain bounded across color-channel and fold-context changes.
- Decode/expansion time and memory decrease materially on a polygon-heavy real tile without shifting the
  hitch into texture upload.
