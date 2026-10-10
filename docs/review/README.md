# Colossus architecture review

Written 2026-10-10. The frontend folder restructure described in [02](02-folder-structure.md) was applied and reviewed the same day; everything else is still review and planning.

This folder reviews the Colossus codebase: what is wrong with it, how to restructure it, and how the system works from the data source to the pixels.

| # | Document | Question it answers |
|---|---|---|
| 1 | [01-critique.md](01-critique.md) | What is wrong, how bad, and is it the problem's fault or the code's? |
| 2 | [02-folder-structure.md](02-folder-structure.md) | What should the repo, frontend and backend look like? Includes the agreed frontend layout and where every file goes. |
| 3 | [03-quick-wins.md](03-quick-wins.md) | What can be fixed cheaply and safely first? |
| 4 | [04-long-term.md](04-long-term.md) | What are the larger goals, and in what order? |
| 5 | [05-how-it-works.md](05-how-it-works.md) | How does it work end to end? Start here to understand the system. |

## Method

- Claims were checked against the code at `34ee146`. The app was run locally and the management screens were captured at 1440px.
- Everything in [`docs/legacy/`](../legacy/) was treated as a claim to verify, never as truth or instruction.

## Frontend naming

The frontend uses plain feature folders, the names most React developers know:
- `app/routes/` for the screens;
- `features/` for the product areas;
- `api/`, `types/`, `components/`, `hooks/`, `lib/`, `config/`, `styles/` and `utils/` for shared code.

The Feature-Sliced and backend-style names used before (`pages`, `widgets`, `entities`, `shared`, `domain`, `infrastructure`) are retired. Details and rules are in [02](02-folder-structure.md#naming).

## Labels used throughout

- **Inherent** — complexity the problem forces: billion-row sources, out-of-core baking, binary tiles uploaded straight to the GPU, measures that must be recomputed per filter. Keep it, document it, test it.
- **Introduced** — complexity that came from an implementation choice. Simplify or delete it.
- **High / Medium / Low** — severity.
- **(unverified)** — nobody executed or confirmed it; treat as a lead, not a fact.

## Legacy docs

Every document that existed before 2026-10-10 now lives in [`docs/legacy/`](../legacy/), unchanged in content:

| Was | Now |
|---|---|
| `docs/*` (AGENTS.md, README.md, archive/, bugs/, evidence/) | `docs/legacy/*` |
| `README.md` | `docs/legacy/root-README.md` |
| `web/README.md` | `docs/legacy/web-README.md` |
| `web/ARCHITECTURE.md` | `docs/legacy/web-ARCHITECTURE.md` |
| `showcase/README.md` | `docs/legacy/showcase-README.md` |

Nothing reads `docs/` at runtime, so the move breaks no build. What it does leave stale: about a dozen code comments and the console message at `src/Colossus.Bake/Program.cs:78` that cite old doc paths (most were already dead), and relative links inside the legacy docs that point into `src/` and `web/`.

Legacy documents worth salvaging into the new docs, because the knowledge exists nowhere else:

- `legacy/archive/2026/2026-08-10-pre-index/companion-scale/SLAB-FORMAT.md` — slab companion binary format.
- `legacy/archive/2026/2026-08-10-pre-index/render-pack/PACK-FORMAT.md` — render pack format and block-order rule.
- `legacy/archive/2026/2026-08-10-pre-index/VIEW_CONFIG.md` — view config and measure language.
- `legacy/archive/2026/2026-08-10-pre-index/RULES.md` — the engine's invariants.
- `legacy/archive/2026/2026-08-10-pre-index/DEPLOY.md` and `legacy/archive/2026/2026-08-20-productized-map-application/10-operational-and-quality-gates.md` — the only deploy knowledge (both stale).
- `legacy/bugs/categorical-color-editor-category-mapping.md` — the one open bug write-up.
