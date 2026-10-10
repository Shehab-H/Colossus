# 04a — Frontend design system and UI architecture

- **Status:** Complete
- **Started:** 2026-08-14
- **Estimate:** 4–6 engineering days
- **Depends on:** Items 01, 03, and 04

## Motivation

The first frontend boundary pass isolated renderer behavior, and the map-management milestone proved the
operator workflows. It did not establish page/widget ownership or a reusable UI system. Management screens
therefore accumulated route orchestration, native control styling, and page layout in one global stylesheet.

## Goal

Establish a maintainable frontend architecture and Vodafone-inspired design system before source discovery,
scheduling, and release management add more product surfaces.

## Engineering direction

- Dependency selection is based solely on fitness, maintainability, accessibility, performance, security,
  ecosystem quality, and operational cost; capable maintained libraries are preferred when they produce the
  stronger system.
- `app`, `pages`, `widgets`, `features`, `entities`, and `shared` have enforced import direction.
- Sass owns authoring-time maps, mixins, and component partials; generated semantic CSS variables support
  runtime themes.
- Shared React primitives own accessible buttons, links, cards, fields, feedback, badges, dialogs, headers,
  and status summaries.
- Product UI uses semantic tokens. Renderer/data-visualization colors may remain purpose-specific.

## Work completed

- Added Sass and a semantic Vodafone-inspired light/dark token system.
- Added shared typed UI primitives and replaced browser confirmation prompts with an accessible modal dialog.
- Introduced page, widget, entity, shared API, and shared theme ownership.
- Migrated the map catalog, map editor, map detail, management shell, and responsive behavior.
- Removed the retired management rules from the global viewer stylesheet.
- Added [`web/ARCHITECTURE.md`](../../../../web/ARCHITECTURE.md) and expanded the executable import rules.
- Moved the route-level map viewer into `pages`, retained renderer mechanics in `features`, and moved the
  viewer shell, embed shell, controls, legend, inspection, and diagnostics into `widgets/map-viewer`.
- Replaced viewer-local form/button/card markup and inline product styling with shared primitives and
  widget-owned Sass; only runtime map/data-visualization values remain inline.
- Reduced `app/styles.scss` to the document reset and unknown-route fallback, and removed all retired renderer
  compatibility token aliases.
- Added a Vodafone-branded Storybook workbench for shared primitives and composed viewer widgets, using the
  same Sass tokens, runtime theme attribute, and self-hosted Inter typography as the application.
- Added Chromium component tests for every story, interaction coverage for confirmation behavior, and
  fail-on-violation axe checks in both light and dark themes.
- Added reviewed Playwright baselines for the design-system showcase and viewer workspace in both themes.
- Split accent, solid-action, and on-soft status color roles after automated contrast testing exposed invalid
  reuse of brand and status colors.
- Replaced manual pathname and history orchestration with React Router data routes, route loaders, canonical
  legacy redirects, typed route links, and route-owned parameters.
- Established TanStack Query as the single owner of remote catalog state, with entity-owned query options,
  feature-owned mutations, loader prefetching, cancellation, and targeted cache invalidation.
- Made management and viewer route modules lazy, moved viewer Sass behind the viewer route boundary, split
  renderer/data dependencies with Rolldown groups, and added a manifest-based initial-bundle contract.

## Outcome

The frontend now has one enforced architecture for structure, UI composition, navigation, server state, and
bundle boundaries. The management control plane no longer loads the map renderer, pages no longer maintain
parallel remote-data caches, and the next product surface can build on stable route and query ownership.

## Acceptance evidence

- Management pages use shared primitives rather than local button, field, card, alert, and badge systems.
- Architecture tests reject imports that point upward through the target layers.
- Desktop light/dark and 390 px mobile passes cover catalog, editor, detail, full viewer, and embed surfaces
  with no horizontal overflow or browser console errors.
- Backend tests, frontend tests, lint, and production build pass.
- Viewer behavior and embed behavior remain unchanged while their UI implementation migrates.
- Storybook component, interaction, and accessibility tests pass in both themes; reviewed visual baselines
  replay without diffs.

## Current evidence

- `dotnet test Colossus.slnx --no-restore`: 166 passed.
- `npm test`: 22 files and 200 tests passed.
- `npm run test:storybook`: 4 files and 8 browser tests passed across light and dark themes.
- `npm run test:visual`: 4 reviewed screenshots passed without diffs.
- `npm run lint`, `npm run build`, and `npm run build:storybook`: passed.
- `npm audit`: 0 vulnerabilities.
- Production bundle contract: 371.8 KiB initial JavaScript; viewer remains a dynamic route and MapLibre stays
  isolated from management screens.
- Browser verification: six catalog rows at desktop and 390 px, create editor, map detail with live embed,
  viewer light/dark switching, collapsed viewer rails, live diagnostics, embed layout, 390 px viewer/embed
  layouts, client-side catalog/editor navigation, canonical legacy viewer redirect, and zero captured console
  warnings or errors.
