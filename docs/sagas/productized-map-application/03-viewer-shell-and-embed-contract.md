# 03 — Viewer shell and embed contract

- **Status:** Candidate
- **Estimate:** 4–6 engineering days
- **Depends on:** Item 01

## Motivation

The current HUD overlays map selection, filters, fidelity, embedding, and diagnostics on the rendering
surface. A product map page needs a stable application layout, while embeds need a smaller contract that
does not load management state or inherit product chrome.

## Goal

Extract one reusable map viewer and host it in two explicit surfaces:

- `/maps/:mapId` — a full map page with controls around a dedicated map window;
- `/embed/:mapId` — a chrome-free, responsive viewer intended for iframes and other applications.

The old `?view=` URL remains a compatibility redirect during migration.

## Integration surface

- [`App.tsx`](../../../web/src/App.tsx) and the current map/view bootstrap.
- [`Hud.tsx`](../../../web/src/components/Hud.tsx),
  [`Controls.tsx`](../../../web/src/components/Controls.tsx),
  [`Legend.tsx`](../../../web/src/components/Legend.tsx), and
  [`InspectPanel.tsx`](../../../web/src/components/InspectPanel.tsx).
- URL/embed helpers under [`web/src/lib`](../../../web/src/lib) and the static embed fixture.
- The map-release lookup API introduced by item 02.

## Required layout and behavior

- Map selection is absent from an individual map page.
- Filters and layer controls live in a left application panel.
- Legend and pinned inspection live in a right panel or responsive drawer.
- Bake/release metadata and performance diagnostics live outside the map and are collapsed by default.
- Only essential geographic controls and attribution may overlay the map canvas.
- The viewer accepts a resolved manifest/release and viewer state; it does not fetch the map catalog.
- URL state remains deterministic for camera, filters, color scale, and theme.
- Performance UI is available only through an explicit debug flag or diagnostics route.

## Embed contract

- Responsive width/height with no fixed product layout assumptions.
- Stable iframe URL and copyable snippet.
- Optional query parameters for camera, filters, color, scale, and theme.
- `postMessage` events for ready, error, camera change, and mark selection, with documented event versions.
- No management API request is needed once the map release is resolved.
- Cross-origin behavior is tested against a separate parent origin.

## Main risks

- Moving React composition can change deck layer identity and undo staged admission.
- Side panels can starve the map of width on small screens unless drawer breakpoints are explicit.
- An unversioned `postMessage` payload becomes a permanent accidental API.

## Acceptance evidence required

- Browser screenshots at desktop and mobile breakpoints show no filters, legend, inspection, map picker,
  or perf dashboard obscuring map data.
- Existing dense polygon and point-map parity checks pass.
- A standalone HTML fixture embeds a map cross-origin and receives versioned ready/selection messages.
- Direct reload and browser navigation preserve URL state on both routes.
- Frontend tests, lint, type-check, and production build pass.
