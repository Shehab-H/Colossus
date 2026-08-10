# 01 — GPU buffer ownership

- **Status:** Candidate
- **Estimate:** 4–7 engineering days

## Goal

Create and own luma.gl buffers when a tile is admitted, hand those buffer handles to deck.gl binary
attributes, and explicitly destroy them when their tile leaves GPU residency. This targets duplicate upload
work, derived-array retention, and GC pressure without replacing stock deck.gl layers.

## Feasibility proof

The installed deck.gl 9.3.6 binary-attribute path accepted an externally created luma.gl buffer and rendered
the expected pixel. The underlying WebGL buffer implementation supports offset writes through
`bufferSubData`.

## Project integration surface

- Replace or extend the typed-array attributes assembled by
  [`tileDeckData`](../../../web/src/lib/deckData.ts).
- Obtain the luma device for both the React `DeckGL` root and the MapLibre `MapboxOverlay` root in
  [`App.tsx`](../../../web/src/App.tsx).
- Add explicit GPU-resource eviction alongside CPU eviction in
  [`TileCache`](../../../web/src/lib/tileCache.ts), or introduce a separate GPU-residency manager with the
  same tile identity.
- Preserve raw tile columns needed for lazy color changes, folding, picking, and inspection.

## Main risks

- A typed-array view is not the expensive memory by itself; its retained `ArrayBuffer` and separately built
  position/filter/expanded arrays are. Memory success must be measured rather than inferred.
- Context loss, view switching, cache eviction, and React unmount must destroy resources exactly once.
- Folded color attributes change by context and need bounded replacement rather than unbounded GPU variants.

## Acceptance evidence required

- Identical render, filter, and click results on point and polygon views.
- One upload per stable attribute; no repeat upload on camera, scale, theme, or filter changes.
- GPU buffers return to baseline after eviction/view change.
- A heap and frame trace demonstrates that the targeted retention or admission cost materially falls.
