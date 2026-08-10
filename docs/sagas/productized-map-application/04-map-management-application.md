# 04 — Map management application

- **Status:** Candidate
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
- Existing [`ViewsController`](../../../src/Colossus.Server/Controllers/ViewsController.cs), replaced or
  versioned behind product-oriented map endpoints.
- A new management feature under [`web/src`](../../../web/src) that remains separate from the reusable
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
