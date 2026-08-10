# Colossus documentation index

This is the entry point for project documentation. The active surface is intentionally small:
documentation for current work lives under `sagas/`; completed or superseded material lives under
`archive/` and remains available as project history.

## Active sagas

| Saga | Status | Next decision |
| --- | --- | --- |
| [001 — GPU rendering stutter](sagas/gpu-rendering-stutter/README.md) | Active; staged-admission mitigation built and verified | Keep explicit GPU ownership deferred unless a single-tile trace crosses the hitch gate |
| [002 — Productized map application](sagas/productized-map-application/README.md) | Active; architecture foundation approved | Complete item 01 before adding catalog, source, UI, or scheduling behavior |

## System map

- [AGENTS.md](AGENTS.md) is the persistent operating memory for this documentation system.
- `sagas/<slug>/README.md` is the index and handoff record for one active initiative.
- Each numbered saga file is one independently decidable work item.
- [archive/README.md](archive/README.md) indexes completed, superseded, and pre-index documentation.

Only the index, operating memory, archive index, and active sagas belong outside the dated archive.
Do not add loose planning or completion files at the root of `docs/`.

## Status vocabulary

- **Candidate** — feasible, but not approved for implementation.
- **Active** — approved and currently being built or validated.
- **Blocked** — cannot progress without a recorded decision or external change.
- **Complete** — acceptance evidence exists; archive it using [AGENTS.md](AGENTS.md).

## Historical reference set

The documentation that predated this index was preserved in the
[2026-08-10 pre-index snapshot](archive/2026/2026-08-10-pre-index/ARCHIVE.md). Its architectural rules,
formats, deployment notes, completed initiatives, and presentation material are historical references,
not active work queues.
