# 03 — Viewer shell and embed contract

- **Status:** Complete
- **Completed:** 2026-08-10
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

- [`App.tsx`](../../../../web/src/app/App.tsx),
  [`MapViewerPage.tsx`](../../../../web/src/features/map-viewer/MapViewerPage.tsx), and the current map/view bootstrap.
- [`EmbedDisclosure.tsx`](../../../../web/src/features/map-viewer/components/EmbedDisclosure.tsx),
  [`Controls.tsx`](../../../../web/src/features/map-viewer/components/Controls.tsx),
  [`Legend.tsx`](../../../../web/src/features/map-viewer/components/Legend.tsx), and
  [`InspectPanel.tsx`](../../../../web/src/features/map-viewer/components/InspectPanel.tsx).
- URL/embed helpers under [`map-viewer`](../../../../web/src/features/map-viewer) and the static embed fixture.
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

## Final outcome

The viewer now has two canonical single-map surfaces. `/maps/:mapId` renders an application shell with
filters and release/embed details on the left, a dedicated responsive map window in the center, and legend,
inspection, and opt-in performance diagnostics on the right. `/embed/:mapId` uses the same renderer without
product or management chrome and keeps its compact legend and selected-mark readout outside the canvas.
The viewer resolves one release directly and no longer fetches the map list.

The old `?view=` and `?embed=1&view=` entry points replace themselves with canonical paths while retaining
camera, filter, color, scale, theme, and debug state. Live full-viewer state remains reloadable in the URL.
The server's compatibility URL response now returns `/maps/:mapId`.

Iframe messages use the documented `colossus.map-viewer` version-1 envelope for `ready`, `error`, `camera`,
and `selection`. The dependency-free parent fixture validates the viewer origin and iframe source and was
verified from a separate origin.

### Public embed contract — version 1

The stable iframe path is `/embed/:mapId`. Optional viewer state uses `lng`, `lat`, `z`, `color`, `scale`,
`scheme`, `bins`, `midpoint`, `reverse`, `theme=dark|light`, and `f_<channel>=<value>` query parameters.

Every parent event contains `source: "colossus.map-viewer"`, `version: 1`, `type`, and `mapId`. `ready` adds
`release`; `error` adds `message`; `camera` adds `{ longitude, latitude, zoom }`; and `selection` adds either
`{ title?, rows: [{ name, value }] }` or `null`. A parent must validate `event.origin`, `event.source`, the
envelope source, and the version before acting. The executable TypeScript contract lives in
[`embedMessages.ts`](../../../../web/src/features/map-viewer/engine/embedMessages.ts).

## Verification evidence

- `npm test` — 19 files and 186 tests passed; `npm run lint` and `npm run build` passed.
- `dotnet test Colossus.slnx --no-restore` — 163 tests passed.
- Browser verification covered the dense `mobile-coverage` polygon map and 1.55M resident `geonames` point
  marks with no console errors.
- A filter selection survived direct reload at its canonical `/maps/:mapId` URL; the legacy GeoNames URL
  redirected to `/maps/geonames` while preserving camera state.
- The cross-origin fixture received version-1 ready, camera, and non-null mark-selection messages.
- [Desktop viewer screenshot](../../../evidence/task03-viewer-desktop.png),
  [mobile viewer screenshot](../../../evidence/task03-viewer-mobile.png), and
  [expanded embed panels screenshot](../../../evidence/task03-embed-panels.png) record the browser evidence.
- The public event schema and parent validation requirements are documented above and enforced by
  [`embedMessages.test.ts`](../../../../web/src/features/map-viewer/engine/embedMessages.test.ts).

## Correction — embed controls and GPU experiment revert

On 2026-08-10 the product owner requested two presentation changes after reviewing the completed viewer.
The full-fidelity/aggregated status wording was removed. The embed continues to use the single-map route,
but now retains map controls and the legend/inspection panel as collapsible side panels on desktop, with responsive drawers below the map on narrow screens.

The separate GPU rendering-stutter saga was also rejected after pan/zoom review. Its staged tile-admission
hook and diagnostic probes were removed; the viewer again submits the complete drawn cover directly to
deck.gl. The rejected implementation and measurements are retained in the
[reverted GPU experiment archive](../2026-08-10-gpu-rendering-stutter-reverted/README.md).
