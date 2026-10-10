# Colossus documentation index

This is the entry point for project documentation. The active surface is intentionally small:
documentation for current work lives under `sagas/`; completed, deferred, or superseded material lives under
`archive/` and remains available as project history.

## Active sagas

None. No forward roadmap is currently approved.

## Candidate bugs

- [Categorical color editor does not provide a usable category-to-color workflow](bugs/categorical-color-editor-category-mapping.md)
  records the observed category-mapping, placeholder, fallback, and computed-measure typing defects. It is
  documented for later prioritization and is not yet approved implementation work.

## Current working mode

Harden the existing, verified design incrementally. New work should start from a concrete observed failure,
operational risk, resource-limit result, or narrowly scoped operator need. Do not revive the deferred broad
operational roadmap or create a replacement full-product plan without an explicit decision.

## System map

- [AGENTS.md](AGENTS.md) is the persistent operating memory for this documentation system.
- `sagas/<slug>/README.md` is the index and handoff record for one active initiative.
- Each numbered saga file is one independently decidable work item.
- `bugs/` contains concrete observed defects awaiting prioritization.
- [archive/README.md](archive/README.md) indexes completed, superseded, and pre-index documentation.

Only the index, operating memory, archive index, and active sagas belong outside the dated archive.
Do not add loose planning or completion files at the root of `docs/`.

## Status vocabulary

- **Candidate** — feasible, but not approved for implementation.
- **Active** — approved and currently being built or validated.
- **Blocked** — cannot progress without a recorded decision or external change.
- **Deferred** — intentionally not scheduled; retain it as historical reference rather than an active queue.
- **Complete** — acceptance evidence exists; archive it using [AGENTS.md](AGENTS.md).

## Historical reference set

The documentation that predated this index was preserved in the
[2026-08-10 pre-index snapshot](archive/2026/2026-08-10-pre-index/ARCHIVE.md). Its architectural rules,
formats, deployment notes, completed initiatives, and presentation material are historical references,
not active work queues.
