# 07 — Trino source adapter

- **Status:** Complete (2026-08-14)
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
  [`Colossus.Infrastructure`](../../../../src/Colossus.Infrastructure).
- Connection/Dataset configuration and map editor fields introduced by items 02 and 04.
- Containerized integration fixtures under [`docker`](../../../../docker) and backend adapter contract tests.

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

## Outcome

The source platform now includes a first-class `trino` adapter backed by a direct implementation of Trino's
statement protocol. It submits one read-only statement, follows every `nextUri`, carries coordinator session
headers forward, applies administrator-owned header profiles, retries only bounded transient HTTP failures,
and sends `DELETE` to the active query when a consumer cancels or stops early. Server query identifiers,
error names/types/codes, warnings, processed rows, and processed bytes remain structured through the source
boundary.

Connection validation, catalog discovery, bounded preview, probe, and extraction all use the same declared
dataset mapping. The adapter safely projects only geometry and channel columns from one `SELECT`, `WITH`,
`TABLE`, or `VALUES` statement; enforces result-page, result-byte, scan-row, row-count, and duration budgets;
and converts Trino values with explicit null, overflow, date, and source-type diagnostics. Point, quadkey,
polygon, and multipart WKT data share the canonical geometry normalizer introduced by the Parquet path.

Probe and extraction stream pages row by row into an on-disk DuckDB materialization. Exact count, bounds, and
distinct geometry are computed without retaining the full result in managed memory; extraction publishes an
atomically renamed Parquet file and reports both row and byte progress. Canonical extraction now uses a total
Morton-plus-column ordering so equal spatial cells remain deterministic across source read orders.

The generic `/sources` workspace renders Trino entirely from the adapter descriptor. The pinned integration
fixture uses Trino 483 with an Iceberg JDBC catalog on PostgreSQL 17.2 and real Parquet data files. Trino writes
the representative dataset, then the Trino and direct Parquet adapters independently probe, extract, and bake
it to byte-identical Arrow output.

## Verification evidence

- `dotnet test Colossus.slnx --no-restore`: 191 passed, zero skipped.
- Direct-protocol tests cover paginated results, response session headers, warnings, bounded transient retry,
  structured query failures, and active-query cancellation.
- Adapter contract tests cover point, quadkey, multipart WKT, explicit conversion overflow, preview scan
  ceilings, read-only SQL enforcement, row/byte progress, cancellation, and partial-file cleanup.
- Pinned `trinodb/trino:483` integration tests use a PostgreSQL-backed Iceberg catalog and Trino-written
  Parquet files; a 20,000-row query produces multiple pages and exact probe/extract results.
- The representative Trino and direct Parquet paths produce byte-identical Arrow tiles.
- `docker compose -f docker/docker-compose.yml config --quiet`: passed.
- `npm run lint`: passed.
- `npm test`: 23 files and 204 tests passed.
- `npm run build`: passed; the executable bundle check reports 372.8 KiB initial JavaScript and a route-lazy
  viewer.
- `npm run test:storybook`: 4 files and 8 light/dark interaction and accessibility tests passed.
- `npm run test:visual`: 4 light/dark visual baselines passed.
- `npm run build:storybook`: passed.
- `npm audit --audit-level=high`: zero vulnerabilities.
- Live API verification returned ClickHouse, Raw Parquet, and Trino descriptors. Live browser verification
  confirmed the generic Vodafone-themed source workspace renders all Trino connection fields without
  persisting test catalog data.
