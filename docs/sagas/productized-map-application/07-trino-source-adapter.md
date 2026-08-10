# 07 — Trino source adapter

- **Status:** Candidate
- **Estimate:** 6–10 engineering days
- **Depends on:** Item 05

## Motivation

Trino opens Colossus to federated catalogs and lakehouse tables without introducing a separate ingestion
copy before every bake. Its paginated statement protocol, session configuration, and type system require a
real adapter rather than ClickHouse SQL substitutions.

## Goal

Add a Trino adapter that validates a connection, discovers schema, previews a query, probes spatial shape,
and streams query results into canonical staging Parquet without retaining the complete result in memory.

## Integration surface

- The adapter platform, canonical staging contract, and preview use cases from item 05.
- A new Trino client/adapter area under
  [`Colossus.Infrastructure`](../../../src/Colossus.Infrastructure).
- Connection/Dataset configuration and map editor fields introduced by items 02 and 04.
- Containerized integration fixtures under [`docker`](../../../docker) and backend adapter contract tests.

## Required behavior

- Configure coordinator URL, catalog, schema, user, optional headers/session properties, and request limits.
- Execute through Trino's paginated HTTP statement protocol and follow `nextUri` until completion.
- Propagate cancellation to Trino and surface server warnings and structured query failures.
- Map Trino scalar/date/string types to Colossus channel types with explicit overflow and null rules.
- Support declared raw SQL while wrapping or projecting it safely for bounded preview, probe, and extract.
- Normalize `lonLat`, `xy`, `quadkey`, and WKT geometry into the canonical staging contract.
- Stream pages to staging with bounded memory and observable row/byte progress.

## Main risks

- Probe wrappers may fail for valid queries with ordering, limits, or connector-specific syntax.
- Trino and connector types can differ from the declared schema; conversion failures must identify the
  channel and source type.
- Coordinator restarts, expired `nextUri` values, and transient connector failures require deliberate retry
  rules. Retrying a partial extract must start a new isolated staging artifact.

## Acceptance evidence required

- Integration tests run against a pinned Trino container and Parquet-backed catalog fixture.
- A multi-page result proves bounded-memory streaming and progress reporting.
- Cancellation removes or cancels the Trino query and leaves no publishable partial staging file.
- Point, quadkey, and WKT fixtures pass the common adapter contract tests.
- A representative dataset bakes to fidelity-equivalent output through Trino and direct Parquet.
