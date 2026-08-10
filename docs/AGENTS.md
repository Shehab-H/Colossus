# Documentation operating memory

These instructions apply to everything under `docs/`. They are the durable memory for how the project
records work; follow them whenever documentation is created, moved, completed, or revived.

## Source of truth

1. `docs/README.md` is the only global index of active work.
2. Every active initiative is a saga at `docs/sagas/<slug>/README.md`.
3. Every independently decidable task inside a saga has one numbered file (`01-...md`, `02-...md`).
4. Completed, abandoned, superseded, and historical material belongs under `docs/archive/<year>/`.
5. The archive is evidence, not a backlog. Never describe archived work as active without explicitly
   reviving it into a new saga.

## Required saga shape

Each saga index records:

- objective and non-goals;
- current status and last verified date;
- ordered work-item index with status;
- evidence already established;
- next decision or action;
- acceptance gates and a concise handoff note.

Each work-item file records its status, motivation, integration surface, risks, acceptance evidence, and
estimate. Keep implementation details close to the item that owns them.

## Archive finished work

Archive in the same change that marks work complete:

1. Add the final outcome and verification evidence to the item.
2. Move the completed item to `docs/archive/<year>/<yyyy-mm-dd>-<saga>/`.
3. Replace its active-saga link with an archive link and remove it from the active queue.
4. Add or update the dated entry in `docs/archive/README.md`.
5. When no active items remain, add a saga completion summary to that dated archive, move the saga index
   there, remove the empty active saga directory, and remove it from `docs/README.md`.

Moves are preferred to copies so there is one canonical document. Preserve history; do not delete finished
evidence merely to make the active tree look clean.

## Change discipline

- Keep only current decisions and next actions in the active tree.
- Use relative Markdown links and repair them whenever a document moves.
- Link source files rather than duplicating source-level contracts into prose.
- Treat archived content as immutable except for link repair, archive metadata, or a clearly labelled
  correction.
- Update the top-level and saga indexes atomically with every lifecycle change.
- Before finishing a docs reorganization, verify that every local Markdown link resolves and inspect
  `git status` for unintended deletions.
