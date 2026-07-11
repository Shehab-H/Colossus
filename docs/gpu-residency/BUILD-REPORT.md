# GPU Residency + Group/Measure — Build Report

Single source of build truth for the one-pass build (README.md order). Per-phase status, commit
hashes, baseline-vs-after numbers, acceptance evidence, and every deviation from the phase docs with
rationale. Maintained continuously; updated before each commit.

## Session log

- Session started on branch `claude/xenodochial-torvalds-f1f0bd` (worktree), base commit `8bfdf18`.
  Confirmed no prior-agent work exists: both sibling worktrees clean, no commits beyond `main`.

## Environment baseline (pre-Phase-1)

Green before any code change:

| Check | Result |
|---|---|
| `dotnet build` | succeeded, 0 warnings |
| `dotnet test` | 93 passed, 0 failed |
| `web: npx tsc -b` | pass |
| `web: npm run lint` (oxlint) | clean |
| `web: npm run test` (vitest) | 79 passed |
| ClickHouse tables | geonames 13,447,008 · ookla_fixed 6,655,986 · mobile_coverage 7,607,947 |

## Phase status

| Phase | Status | Commit | Notes |
|---|---|---|---|
| 0 — Baseline | done | _(this commit)_ | build/test green; browser baseline in `BASELINE.md`; geonames+ookla-fixed baked, `verify` PASS |
| 1 — GPU filtering | not started | — | |
| 2 — GPU color | not started | — | |
| 3 — Zero-copy tiles v2 | not started | — | |
| 4 — Group/measure | not started | — | |

## Deviations from phase docs

_(none yet)_

## Phase 0 — Baseline

See `BASELINE.md` for the measurement table. Method + numbers recorded there and re-measured after
each landed phase.
