# 04 — Staged layer admission

- **Status:** Built and browser-verified
- **Implemented:** 2026-08-10

## Goal

Keep a dense tile-cover replacement from allocating and uploading every deck.gl layer in one animation
frame, without changing pixels, picking, filters, cover atomicity, or GPU-resource ownership.

## Measured cause

The expanded entry-gate probes isolated the main-thread handoff:

- worker response delivery: p95 46 ms in the instrumented dense load;
- cache admission: p95 0.1 ms;
- layer construction: p95 1.9 ms;
- React render work: p95 6.2 ms;
- deck attribute rebuild/upload: 81.6 ms for the dense six-layer cover.

The cache and React work were too small to own the hitch. The visible stall was deck allocating and
uploading geometry/value/filter attributes for several newly drawable layers in the same frame.

## Implementation

[`useStagedTileAdmission`](../../../web/src/features/map-viewer/hooks/useStagedTileAdmission.ts) keeps the last complete cover
visible, mounts replacement layers with `visible: false` one per animation frame, and flips the already
resident replacement cover visible atomically after the final warm-up frame. During an active geographic
pan or zoom, admission is paused so a completed worker response cannot inject an upload into the gesture.

The first cover has no stand-in and therefore paints progressively. Same-layer-id data replacements are
also admitted one per frame, because old and new variants cannot coexist under the same deck layer id.

The perf surface now records worker delivery, cache admission, React commits, and deck attribute work, so
future regressions can be classified without inferring main-thread cost from worker timers.

## Browser evidence

The representative case is `mobile-coverage` over central Europe with 425,765 resident polygon cells on
an AMD Radeon RX 6750 XT.

| Run | FPS | Long frames | Max frame | Result |
| --- | ---: | ---: | ---: | --- |
| Before staging | 13 | 12 | 1008 ms | Visible pan hitch |
| After staging | 125 | 0 | 24 ms | Below the 50 ms hitch threshold |

The before capture includes the automated gesture interval and is retained as symptom evidence rather
than a microbenchmark. The after capture used the same viewport and a dense adjacent-cover pan. Its two
deck-attribute samples totalled 73 ms: 61.2 ms for initial admission and about 11.8 ms added by the pan,
with no frame at or above 50 ms.

Evidence: [before](evidence/mobile-coverage-dense-pan-before.jpg) and
[after](evidence/mobile-coverage-dense-pan-after.jpg).

Correctness checks also covered:

- the polygon view at full fidelity after pan and wheel zoom;
- a GPU-filter selection and restoration;
- the `geonames` point path with 1,142,323 resident marks at 143 fps, zero long frames, and a 21 ms max;
- point picking/inspection (`Arnisee`, feature class `H`, feature code `LK`, country `CH`).

Point-path evidence: [geonames parity](evidence/geonames-point-parity.jpg).

## Verification

- `npm test`: 16 files, 180 tests passed.
- `npm run lint`: passed.
- `npm run build`: TypeScript and Vite production build passed.

## Remaining boundary

This removes dense attribute admission from active pan/zoom frames; it does not replace deck's buffer
ownership. Item 1 remains the next escalation only if a later trace shows a single staged tile still
crossing the hitch threshold, GPU residency leaking, or repeated uploads for stable attributes.
