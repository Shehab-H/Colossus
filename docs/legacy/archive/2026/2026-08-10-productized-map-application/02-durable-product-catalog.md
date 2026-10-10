# 02 — Durable product catalog

- **Status:** Complete
- **Completed:** 2026-08-10
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

- Replace product use of [`IViewCatalog`](../../../../src/Colossus.Application/Abstractions/Baking/IViewCatalog.cs) with
  application query/command ports such as `IMapRepository` and `IConnectionRepository`.
- Keep [`ViewConfig`](../../../../src/Colossus.Domain/Model/ViewConfig.cs) as a bake/render snapshot during
  migration, derived from catalog state rather than used as the mutable aggregate.
- Replace filesystem writes in [`ViewsController`](../../../../src/Colossus.Server/Controllers/ViewsController.cs)
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

## Final outcome

The product catalog is now a real relational control plane. Provider-independent repository ports and
application use cases operate on explicit Connection, Dataset, Map, BakePolicy, BakeRun, and Release records.
The initial Infrastructure provider is embedded SQLite, selected so the single-operator product has restart
durability, transactions, unique constraints, foreign keys, and optimistic concurrency without requiring an
external service. Workspace keys are present on mutable records even though this phase uses the `default`
workspace.

Connections own adapter identity and non-secret settings; datasets own query, geometry, and channels without
duplicating the adapter; maps own presentation and editable draft state. A `ViewConfig` is derived only when a
bake or compatibility read needs an immutable snapshot. Bake runs persist that snapshot beside the captured
map revision, so later edits cannot rewrite run history. Bake policies store provider-neutral interval, time
zone, overlap, and retention values; releases are immutable and constrained to a run belonging to the same map.

The server exposes secret-safe connection, dataset, map, and bake-policy endpoints. Connection responses are
dedicated projections that omit both secret input and settings, and secret-like keys are rejected from the
public settings bag. The old `/api/views` reads and `?view=<slug>` URLs now resolve through a read-only catalog
compatibility adapter. Product writes no longer use the file registry, and the obsolete registry implementation
was removed.

Startup in both hosts creates the schema and idempotently imports authored `views/*.json`. The importer never
edits or removes those files, shares one imported ClickHouse connection across the six current datasets, and
derives serialization-equivalent bake snapshots. Repeated startup recognizes already imported slugs.

## Verification evidence

- `dotnet test Colossus.slnx --no-restore` — 163 tests passed, including repository CRUD/archive,
  relationship constraints, duplicate slugs, stale revisions, policy persistence, immutable run snapshots,
  release ownership, restart persistence, all-view migration, legacy URL lookup, and HTTP secret exclusion.
- `dotnet format Colossus.slnx --verify-no-changes --no-restore` — passed.
- `dotnet list Colossus.slnx package --vulnerable --include-transitive` — every project reported no known
  vulnerable packages. The native SQLite bundle is explicitly pinned to patched `2.1.12` rather than the
  vulnerable transitive version first selected by the provider.
- `npm test -- --run` — 18 files and 184 tests passed; `npm run lint` and `npm run build` passed.
- Migration evidence covered all six current JSON views twice: first import added six maps, the second added
  zero, every derived snapshot matched canonical serialization, and every source file remained byte-identical.

## Deliberate follow-ups

- The bake and fidelity hosts retain the read-only `IViewCatalog` compatibility port. Hangfire execution in
  item 08 will consume captured `BakeRun.Definition` directly and can then remove that shim.
- SQLite is the initial provider, not an Application dependency. A future provider implements the same six
  repository ports without changing domain or API contracts.
- Operational backup, file-permission, logging, and secret-handling drills remain owned by item 10; no catalog
  secret is exposed by current response contracts or record string representations.

## Correction — 2026-08-10 provider decision

Before the next product item began, the deployment decision changed from embedded SQLite to Docker-hosted
MySQL and from environment overrides to appsettings-only runtime configuration. The provider-independent
Application contracts and product model described above remain current; the SQLite-specific implementation,
package, and verification statements are historical evidence of the first completed version. The superseding
implementation and evidence are recorded in
[item 02a — MySQL database and appsettings configuration](02a-mysql-and-appsettings-configuration.md).
