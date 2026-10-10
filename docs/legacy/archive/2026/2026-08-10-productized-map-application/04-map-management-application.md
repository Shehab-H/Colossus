# 04 — Map management application

- **Status:** Complete
- **Completed:** 2026-08-10
- **Estimate:** 5–8 engineering days
- **Depends on:** Items 02 and 03

## Motivation

Maps are currently selected inside the rendering HUD and registered by POSTing a complete JSON document.
Operators need a dedicated place to create, edit, schedule, run, inspect, open, embed, and archive maps.

## Goal

Build `/maps` as the product home and add focused create/edit/detail flows backed by application use cases,
not direct registry files.

## Integration surface

- Catalog commands and queries introduced by item 02.
- Existing [`ViewsController`](../../../../src/Colossus.Server/Controllers/ViewsController.cs), replaced or
  versioned behind product-oriented map endpoints.
- A new management feature under [`web/src`](../../../../web/src) that remains separate from the reusable
  viewer extracted by item 03.
- Bake-run and release read models owned by items 08 and 09.

## Required behavior

- Map list with title, source type, publication state, current release, last run, next run, last failure,
  output size, and actions.
- Create/edit flow for dataset selection, geometry, channels, measures, filters, encoding, initial viewport,
  schedule, and retention.
- Map detail page with preview/open/embed, run-now, enable/disable schedule, run history, and releases.
- Archive instead of immediate destructive deletion.
- Validation errors tied to their fields and unsaved-change protection.
- One individual map viewer never offers a switcher for other maps.

The initial application may use technical labels and raw SQL where appropriate. A polished no-code builder
is not required until the source discovery and preview contract in item 05 is available.

## Main risks

- Reimplementing domain validation in React can drift from server authority. API validation must remain
  canonical and return structured field errors.
- Job and release status may arrive after this UI task; temporary states must map cleanly to the eventual
  domain rather than use hard-coded display guesses.
- Archive and delete controls must not remove published artifacts directly.

## Acceptance evidence required

- Browser flow creates a map, edits it, opens its viewer, copies an embed, and archives it without touching
  JSON or the command line.
- Component/API tests cover empty, loading, validation, failed-run, never-baked, and published states.
- The page remains usable at desktop and mobile breakpoints and meets keyboard/focus basics.
- Existing registered views appear after the catalog migration.

## Final outcome

`/maps` is now the product home. It lists durable catalog maps and their source, publication, run,
schedule, release, failure, and output-metadata state without loading that catalog into an individual
viewer. Dedicated create, edit, and detail routes cover dataset selection, rendering configuration,
initial viewport, schedule, retention, preview/open/embed actions, immutable run snapshots, history,
releases, and archival.

The product-facing queries and run-now command live in
[`MapManagementService`](../../../../src/Colossus.Application/Catalog/MapManagementService.cs). The API
returns canonical structured field errors, while the editor adds immediate JSON/form feedback and protects
dirty drafts from accidental navigation. All six legacy views appear through the catalog migration. Run
execution remains queued for item 08, and release output sizes remain explicitly pending until item 09
owns artifact metadata; the management model already represents those states without display guesses.

## Verification evidence

- `dotnet test Colossus.slnx --no-restore` passed 166 tests, including API flows for create, list,
  validation, policy, queue, detail, and archive, plus application coverage for failed and published maps.
- `npm test` passed 22 files and 200 tests; `npm run lint` and `npm run build` passed.
- Browser verification created and edited a map, opened its viewer, copied its embed, queued a run, enabled
  its schedule, inspected history, and archived it without editing JSON or invoking a command.
- The six migrated maps appeared in the catalog. A 390 x 844 browser pass retained all six rows, the create
  action, and zero horizontal overflow.
- [Desktop catalog screenshot](../../../evidence/task04-map-catalog-desktop.png),
  [mobile catalog screenshot](../../../evidence/task04-map-catalog-mobile.png), and
  [map detail screenshot](../../../evidence/task04-map-detail.png) record the browser evidence.
