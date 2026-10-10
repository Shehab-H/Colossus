# Saga 001 — GPU rendering stutter

- **Status:** Reverted and archived at product-owner request
- **Reverted:** 2026-08-10
- **Created:** 2026-08-10
- **Last verified:** 2026-08-10

## Objective

Remove the remaining visible frontend hitch associated with deck.gl tile admission while preserving exact
pixels, GPU filtering, color LUT behavior, click inspection, tile-cover transitions, and bounded residency.

This saga retains only the three WebGL techniques proven against the installed deck.gl/luma.gl 9.3.6 stack.
WebGPU, SharedArrayBuffer, and LOD delta encoding are outside its scope.

## Work-item index

| Order | Item | Status | Estimated production work |
| --- | --- | --- | ---: |
| 0 | [Staged layer admission](04-staged-layer-admission.md) | Reverted | Implemented, then removed |
| 1 | [GPU buffer ownership](01-gpu-buffer-ownership.md) | Abandoned | 4–7 engineering days |
| 2 | [Arena allocation and multi-draw](02-arena-multi-draw.md) | Abandoned | 10–15 engineering days |
| 3 | [Vertex pulling and data textures](03-vertex-pulling.md) | Abandoned | 6–10 additional engineering days |

All three together are estimated at 4–6 engineering weeks for one experienced developer, including
integration tests, real-tile profiling, and browser/GPU hardening. Items 2 and 3 are naturally coupled; the
combined design should reuse item 1's explicit resource-lifetime work rather than build it twice.

## Evidence already established

A disposable browser harness ran against the live local frontend and was removed afterward. No production
files were changed.

- An externally owned luma.gl buffer rendered through a stock deck.gl binary attribute; sampled center
  pixel: `[255, 32, 16, 255]`.
- `WEBGL_multi_draw` was available and rendered two independent ranges correctly.
- A vertex shader used `texelFetch` with a mark ID to produce distinct per-mark pixels. The tested device
  exposed 16 vertex texture units and a maximum texture size of 16,384.

These results prove mechanism compatibility on the tested machine. They do not prove which mechanism fixes
the product hitch or that the same extension is available on every supported GPU.

## Entry gate

Before approving implementation, capture a repeatable real-tile trace with `?perf=1` and classify the
largest admission frame as one or more of:

1. deck attribute upload or GPU buffer allocation;
2. layer construction/draw-call overhead;
3. polygon per-vertex color/filter expansion;
4. another cost outside this saga.

Only start the item whose measured cost can pay for its complexity. Item 2 must retain a fallback for
devices without `WEBGL_multi_draw`.

### Baseline capture protocol

Use the already-instrumented local frontend at
`http://127.0.0.1:5173/?perf=1&view=geonames` with the API on port 5174. For a repeatable sample:

1. let the initial tile cover settle and wait for deck's once-per-second metrics;
2. press **clear**, then pan into one adjacent uncached tile cover without changing color, filters, or theme;
3. wait for the replacement cover to settle and run `__perf.dump()` in the browser console;
4. retain the frame maximum, long-frame count, stage statistics, and deck `updateAttributesTime` value;
5. repeat three times against the same viewport, including one genuinely cold run from the dashboard.

Do not mark item 1 Active until this evidence shows that attribute allocation/upload or retained derived
arrays are a material part of the admission hitch. If layer count or polygon expansion dominates instead,
activate item 2 or 3 respectively.

### Entry-gate result — 2026-08-10

Three `geonames` admissions were captured on an AMD Radeon RX 6750 XT. The second warm run was sampled
during deck's admission second rather than after its gauge returned to idle.

| Run | Tiles fetched | Long frames | Max frame | Attribute upload | `inflate` p95 | `decode.tile` p95 | `layers` p95 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Warm pan 1 | 12 | 0 | 14 ms | 0.0 ms at settled capture | 14.4 ms | 28.5 ms | 0.5 ms |
| Warm pan 2 | 4 | 0 | 45 ms | 1.9 ms | 10.3 ms | 19.0 ms | 0.5 ms |
| Cold reload | 13 | 4 | 98 ms | 6.3 ms | 41.3 ms | 26.1 ms | 2.0 ms |

Evidence screenshots: [warm run 1](evidence/2026-08-10-warm-run-1.png),
[warm run 2 admission peak](evidence/2026-08-10-warm-run-2-peak.png), and
[cold admission peak](evidence/2026-08-10-cold-run-peak.png).

**Initial classification:** another cost outside the three retained GPU techniques. In the only visibly bad run,
attribute upload was about 6.4% of the 98 ms maximum frame and layer construction p95 was 2.0 ms. Even
perfectly eliminating both cannot meet the saga's 2× improvement gate. The larger recorded work is
decompression and tile decode, but those timers run in the worker and therefore do not by themselves
explain a main-thread frame. The missing measurement is the worker-response → structured-clone delivery →
cache commit → React/deck commit interval when many tiles resolve together.

Item 1 remains Candidate. Items 2 and 3 also remain Candidate because neither layer overhead nor polygon
expansion was shown to dominate. The next decision is whether to open a separate cold-admission delivery
investigation and instrument that interval; implementing GPU ownership from this evidence is not approved.

### Expanded entry-gate result — dense polygon view

The approved follow-up added probes for worker delivery, cache admission, React commits, and deck's
attribute counter, then repeated the trace on `mobile-coverage` with 425,765 resident polygon cells. This
found the cost the GeoNames runs did not expose: deck attribute rebuild/upload reached 81.6 ms for the
dense six-layer cover, while cache admission remained at p95 0.1 ms, layer construction at p95 1.9 ms,
and React render work at p95 6.2 ms.

The smallest solution that met the interaction gate was not external buffer ownership. New covers are now
admitted hidden one layer per animation frame, kept out of active pan/zoom gestures, and revealed only
after the replacement cover is resident. The matching dense pan completed at 125 fps with zero long
frames and a 24 ms maximum. See [item 04](04-staged-layer-admission.md) for the implementation and evidence.

Item 1 remains a deferred escalation: use it only if a single staged tile exceeds the hitch threshold or
explicit ownership is needed for GPU-residency cleanup. Items 2 and 3 remain deferred.

## Global acceptance gates

- Pixel output, filtering, color scales, picking, and inspection match the current renderer.
- Tile-cover transitions do not flash, drop, or double-count marks.
- No GPU resources survive tile eviction, view changes, WebGL context loss, or unmount.
- The targeted admission cost falls materially on a representative real tile and the worst visible frame
  improves by at least 2× or falls below the agreed hitch threshold.
- Frontend tests, type-check, lint, and production build pass.

## Revert outcome — 2026-08-10

The product owner reported that pan and zoom felt wrong with staged admission and requested that every
runtime change belonging to this saga be reverted. The staged-admission hook, hidden-layer warm-up,
interaction pause, worker-delivery/cache-admission/React/deck-attribute probes, and perf-only React
Profiler were removed. The renderer again passes the complete drawn tile cover directly to deck.gl.

The documentation-system reorganization made in the same historical commit was not a rendering change
and remains in place. This saga and its measurements are archived as rejected experimental evidence, not
as an active backlog or a performance baseline.

## Handoff memory

The saga was picked up and its original GeoNames entry gate completed on 2026-08-10. A follow-up dense
polygon trace then isolated an 81.6 ms deck attribute-admission burst. Item 04 now stages layer admission
one layer per frame and pauses it during active geographic interaction. The verified 425,765-cell pan ran
at 125 fps with zero long frames and a 24 ms maximum; the 1,142,323-mark point path and inspection also
passed browser verification.

Binary tile data still enters stock deck.gl layers through
[`tileDeckData`](../../../../web/src/features/map-viewer/engine/deckData.ts); no custom renderer or externally owned GPU buffer was
introduced. Keep item 1 as the next measured escalation only if a future single-tile admission exceeds
the threshold or resource lifetime requires explicit ownership.

That implementation guidance is historical. The staged solution was subsequently rejected and reverted;
do not revive any item from this archive without a new product decision and a newly scoped active saga.

The original completed GPU-residency record and deferred design are preserved in the
[pre-index archive](../2026-08-10-pre-index/gpu-residency/README.md).
