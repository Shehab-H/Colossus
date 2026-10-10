# 3. Quick wins

These are small, low-risk changes, ordered by what to do first. Effort: **S** is under an hour, **M** is under a day. Apart from 0.8 and the parts the folder restructure covers (marked), nothing here has been done yet.

Frontend paths below are as they were before the folder restructure. [02](02-folder-structure.md#where-todays-code-goes) maps each one to its new home.

## Batch 0 — environment and hygiene (no code)

| # | Action | Effort | Risk |
|---|---|---|---|
| 0.1 | Delete local build output: every `bin/obj` (~3.3 GB), `publish/`, `publish-x86/`, `web/dist`, `web/storybook-static`, `web/test-results`, `.vs`, `.codex-run`, root `bin/obj`, the stale `src/Colossus.Server/wwwroot`, and the empty `src/Colossus.Server/tiles/` | S | None to source; everything is regenerable. Confirm nobody still needs the July `publish/` folder or `web/public/__perf-harness.js` |
| 0.2 | Remove the crash-looping `colossus-server-dev` container and the old `colossus-server-dev-pre-numeric` one | S | None. Neither is in compose, and the dev one mounts a folder that no longer holds a server |
| 0.3 | Remove the two `.claude/worktrees` with `git worktree remove` (2.2 GB) | S | One worktree has an uncommitted `.claude/launch.json` change; look at it first |
| 0.4 | Extend `.gitignore` to cover `.codex-run/`, `.claude/worktrees/` and `.claude/settings.local.json` (today only the local `.git/info/exclude` hides them). Add `storybook-static`, `test-results`, `playwright-report` and `__perf-harness.js` to `web/.gitignore` | S | None |
| 0.5 | Prune ~1.9 GB of superseded tile versions with the app's retention action (`POST /api/maps/{id}/releases/reconcile`), not by hand | S | Retention is tracked in MySQL. Deleting folders by hand leaves release rows pointing at nothing |
| 0.6 | Decide the fate of the 15 branches. Back up `saga/exact-numeric-filtering` (it holds unmerged work), then delete the branches that are 0 ahead of `main` | S | Low once backed up |
| 0.7 | Delete the empty `.github/workflows/` folder or replace it with real CI (item 5.1) | S | None |
| 0.8 | Legacy docs moved to `docs/legacy/` — **done 2026-10-10** | — | — |

## Batch 1 — security stopgaps (small code, do before anything is redeployed)

| # | Action | Where | Effort |
|---|---|---|---|
| 1.1 | Require auth on every mutating route and on Swagger. An API key or the organisation's identity provider is enough as a stopgap. Restrict CORS on writes to the app's own origin | `src/Colossus.Server/Program.cs:35-63` | M |
| 1.2 | Production: turn off public Swagger and the unauthenticated `POST /api/views` on `colossus.shehabhassan.space`, or put the host behind auth | deployed July build | S |
| 1.3 | Stop publishing source SQL and settings: strip `view.source.query`, `source.settings` and `bakeFilters` from the public manifest | `BakeViewUseCase.cs:159`, `Manifest.cs:17` | S |
| 1.4 | Stop serving `facts.parquet` publicly: drop `ServeUnknownFileTypes`, or keep facts outside the public tiles root | `Program.cs:95` | S |
| 1.5 | Validate `id` and `version` as safe path segments in the fold endpoint; cap the tile count and add a concurrency limit | `FoldController.cs:28-33` | S |
| 1.6 | Make unknown `/api/*` routes return a JSON 404 instead of the SPA's HTML | `Program.cs:130` | S |

## Batch 2 — map editor fixes (small frontend code, the most visible pain)

| # | Action | Where |
|---|---|---|
| 2.1 | Fix the SQL and category-order textareas: keep the raw text in local state and normalise on blur | `MapConfigurationBuilder.tsx:104-106`, `ColorEncodingEditor.tsx:213` |
| 2.2 | Give override rows stable ids so the name input keeps focus | `ColorEncodingEditor.tsx:230` |
| 2.3 | Derive a measure's colour kind with the existing `parseMeasure`, so argmax/argmin offer categorical scales | `colorEncoding.ts:29-31` |
| 2.4 | Carry fields the form doesn't model (`reduction`, `aggregate`, `storage`) through edit and save | `mapForm.ts:44-64,91-106` |
| 2.5 | Remove the hard-coded `Category 1…4` preview padding; preview with the real categories from the dataset's last manifest | `ColorEncodingEditor.tsx:268` |
| 2.6 | Replace the single-option "control" dropdown with nothing (or single/multi select). Replace the `min..max` default box with the viewer's existing range and date inputs | `MapConfigurationBuilder.tsx:121-124`, `Controls.tsx` |
| 2.7 | Validate measure expressions inline with the client parser, and suggest channel names | `measures.ts:42` |
| 2.8 | Offer only marks the viewer can draw (polygon, point) | `MapEditorPage.tsx:24` |
| 2.9 | Schedule presets (hourly / 6 h / daily / weekly / custom) instead of four number boxes | `MapEditorPage.tsx:219-222` |
| 2.10 | Derive slugs from titles, reserve `new`, use one slug rule everywhere | `mapForm.ts:77`, `sourceForms.ts:125,149` |
| 2.11 | Add a sticky save bar and a router-level unsaved-changes guard | `MapEditorPage.tsx` |
| 2.12 | Wording pass: replace jargon; humanise statuses, intervals and source types; show "Not published yet" instead of the raw 404; replace raw exception text with a readable message plus a "details" disclosure; collapse old bake runs; clear "Last failure" after a later success | management pages |
| 2.13 | Turn "Discover locations" results into "Use this table" (prefills the query). After saving a dataset, offer "Create map from this dataset" | `SourceConnectionList.tsx:44-49`, `SourceWorkspacePage.tsx:146` |
| 2.14 | Disable "Open" and "Copy embed" until a map has a first release | `MapCatalogTable.tsx`, `MapDetailPage.tsx` |

## Batch 3 — frontend code health (no behaviour change)

| # | Action | Where |
|---|---|---|
| 3.1 | Lazy-load the perf dashboard; install `window.__perf` and resource timing only with `?perf=1`; stop shipping `embed.html` and `__perf-harness.js` | `main.tsx:19-21`, `MapViewerPage.tsx:13`, `web/public/` |
| 3.2 | Delete or repair the broken bench scripts | `web/scripts/bench-*.ts` |
| 3.3 | Delete dead `perf.timed`; declare `@luma.gl/core` in `package.json`. (Deleting the dead `domain/map/view.ts` is part of the restructure.) | |
| 3.4 | Type-check tests (stop excluding `*.test.ts` from `tsc`) | `tsconfig.app.json:36` |
| 3.5 | One constants module for the `2.0e38` sentinel, `'(all)'` and the numeric-type set | `dates.ts`, `gpuFilter.ts`, `measures.ts`, `channels.ts`, `colorEncoding.ts`, `embed.ts`, `viewRoute.ts` |
| 3.6 | Add the missing `.catch` that blanks the map silently | `useViewData.ts:81-86` |
| 3.7 | Require `VITE_API_BASE` and `VITE_TILES_BASE` in production builds; drop the regex derivation | `shared/api/config.ts:1-2` (after the restructure: `api/client.ts`) |
| 3.8 | ~~One path-alias table instead of four copies~~ — covered by the restructure, which replaces the eight per-layer aliases with one `@/` alias | `vite.config.ts`, `vitest.config.ts`, `tsconfig.app.json`, `tsconfig.storybook.json` |
| 3.9 | Fix stale comments citing `lib/` and `swClient.ts`. (Moving `colors.test.ts` and `colorScale.test.ts` next to `lib/color/` is part of the restructure.) | `features/map-viewer/engine/` |

## Batch 4 — contract (cheap steps toward a safe API boundary)

| # | Action |
|---|---|
| 4.1 | Annotate controllers with response types and commit the OpenAPI document as a snapshot. A test fails when the API changes without the snapshot changing |
| 4.2 | Serialise intervals as integer seconds (or ISO-8601) instead of .NET `d.hh:mm:ss` |
| 4.3 | Decide on `schemaVersion`: either check it on both sides, or delete it |
| 4.4 | Add one golden manifest and one golden Arrow tile, written by C#, and make the TypeScript tests parse them |

## Batch 5 — workflow

| # | Action | Note |
|---|---|---|
| 5.1 | Minimal CI: frontend lint + unit tests + build (the bundle budget gate runs in build); backend unit tests that need no Docker | Tag Docker tests `[Trait("Category","Docker")]` first so CI can exclude them |
| 5.2 | `global.json` (pin the SDK), `.editorconfig`, `.gitattributes`, `.nvmrc` / `engines` | `.gitattributes` causes a one-off line-ending diff |
| 5.3 | One port per app: web 5173, API 5174, everywhere | `launchSettings.json`, `.claude/launch.json` |
| 5.4 | Make the Trino integration test write to a temp folder instead of `docker/trino/data` | Needs a matching compose mount |
| 5.5 | Write the new `README.md`, `CLAUDE.md` and `AGENTS.md`: commands, boundaries, rules, and links to these docs | Pending |

Leave alone for now: rewriting history to drop the 71.6 MB video. It saves ~70 MB per clone but changes every commit hash. Only worth doing before many new clones are made, if at all.
