# 02 — Durable product catalog

- **Status:** Candidate
- **Estimate:** 4–6 engineering days
- **Depends on:** Item 01

## Motivation

The current registry stores each complete view as `views/<id>.json`. That is adequate for a demo but cannot
reliably represent a reusable connection, a dataset shared by several maps, a bake schedule, job history,
draft edits, or published releases.

## Goal

Introduce a durable, provider-independent product catalog with explicit records for Connection, Dataset,
Map, BakePolicy, BakeRun, and Release. Authentication is out of scope, so records belong to the single
installation; identifiers and repository contracts must still allow a later owner/workspace key.

## Integration surface

- Replace product use of [`IViewCatalog`](../../../src/Colossus.Domain/Baking/IViewCatalog.cs) with
  application query/command ports such as `IMapRepository` and `IConnectionRepository`.
- Keep [`ViewConfig`](../../../src/Colossus.Domain/Model/ViewConfig.cs) as a bake/render snapshot during
  migration, derived from catalog state rather than used as the mutable aggregate.
- Replace filesystem writes in [`ViewsController`](../../../src/Colossus.Server/Controllers/ViewsController.cs)
  with application use cases.

## Required behavior

- Create, read, update, archive, and list maps.
- Reuse one connection and dataset across multiple maps.
- Validate unique slugs and optimistic concurrency for edits.
- Persist schedule and retention settings without depending on Hangfire types.
- Record draft revision separately from the immutable definition captured by each bake run.
- Import current `views/*.json` records idempotently so existing maps remain available.
- Never return connection secrets in list/detail API responses or logs.

## Main risks

- Treating an editable map as the release definition would make historical bakes irreproducible.
- Leaking ORM entities through application/API contracts would couple every later item to one database.
- Destructive migration of `views/*.json` would remove the rollback path.

## Acceptance evidence required

- Repository contract tests cover create/update/list/archive, concurrency, and relationship invariants.
- A migration test imports every current view and derives an equivalent bake snapshot.
- Restart persistence is demonstrated with the selected relational provider.
- API tests prove secret fields are never serialized.
- Existing view URLs continue to resolve through a compatibility redirect or lookup.
