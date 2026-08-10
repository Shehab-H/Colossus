# 06 — Raw Parquet source adapter

- **Status:** Candidate
- **Estimate:** 4–6 engineering days
- **Depends on:** Item 05

## Motivation

Many useful datasets already exist as Parquet and should not require loading into ClickHouse before a map
can be baked. Parquet is also the simplest independent proof that the source platform is not tied to a
remote SQL engine.

## Goal

Add a DuckDB-backed Parquet adapter for a single file or partitioned dataset, supporting local paths and
HTTP/S3-compatible locations through provider configuration.

## Integration surface

- The adapter registration and discovery/preview/extract contracts from item 05.
- Existing DuckDB infrastructure under [`Colossus.Infrastructure/DuckDb`](../../../src/Colossus.Infrastructure/DuckDb).
- Connection/Dataset configuration and map editor fields introduced by items 02 and 04.
- Shared geometry and canonical staging fixtures used by ClickHouse adapter contract tests.

## Required behavior

- Discover schema and sample rows without reading all row groups.
- Accept one file, a safe glob, or a partitioned dataset root.
- Project only declared geometry/channel columns and push filters into DuckDB where possible.
- Support `lonLat`, `xy`, `quadkey`, and WKT geometry mappings consistent with existing semantics.
- Produce canonical staging data with deterministic type conversion, null behavior, bounds, count, and
  distinct-geometry probe results.
- Treat Parquet as immutable input for one run; record the resolved file set or source fingerprint with the
  bake snapshot.
- Report missing files, schema drift, unsupported logical types, and inaccessible remote objects clearly.

An optional SQL projection may be supported through a bounded DuckDB query, but raw location + mapping is
the primary workflow. Arbitrary filesystem access from browser input is not part of the adapter contract;
the server resolves configured roots and locations.

## Main risks

- Partitioned datasets can change during a run unless the resolved inputs are captured.
- Remote Parquet performance depends on HTTP range support and selective projection.
- Eager WKT parsing can dominate probe and preview latency.

## Acceptance evidence required

- Contract fixtures cover point, quadkey, multipart WKT, partitioned input, type coercion, and nulls.
- Local, HTTP, and configured S3-compatible reads pass integration tests where supported by CI.
- Column/predicate projection is demonstrated from DuckDB query plans or read metrics.
- The same representative Parquet dataset produces fidelity-equivalent tiles to its ClickHouse version.
- Cancellation and bounded preview behavior are verified.
