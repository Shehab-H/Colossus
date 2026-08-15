# 06 — Raw Parquet source adapter

- **Status:** Complete (2026-08-14)
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
- Existing DuckDB infrastructure under [`Colossus.Infrastructure/DuckDb`](../../../../src/Colossus.Infrastructure/DuckDb).
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

## Outcome

The source platform now includes a DuckDB-backed `parquet` adapter for server-configured local, HTTP, and
S3-compatible providers. Browser-authored connections select a declared provider alias; dataset locations
must remain relative to that provider, and local paths are expanded beneath the configured root with
traversal and reparse-point protection. Single files, safe `*`/`?` globs, recursive globs, and partitioned
dataset roots are supported without exposing arbitrary server filesystem access.

Preview reads Parquet metadata before data pages, enforces row, byte, and duration ceilings, and selects only
mapped geometry/channel columns so DuckDB can apply Parquet projection and filter pushdown. Canonical point,
quadkey, polygon, and multipart polygon normalization share deterministic type conversion and null rules;
WKT uses NetTopologySuite rather than adapter-specific SQL. Full extraction materializes out of core, orders
rows by a two-dimensional Morton key, writes through an atomic partial file, and reports cancellation and
completion through the source contract.

Every bake resolves its logical input set before probing and records that set plus a SHA-256 source
fingerprint in the immutable source snapshot. Probe and extraction revalidate the fingerprint so a changed or
missing local/HTTP input cannot silently continue an already captured bake. S3 endpoints and credentials are
administrator-owned provider configuration and are passed to DuckDB's temporary secret manager; they never
enter the browser or catalog settings.

The generic `/sources` workflow now consumes adapter-declared provider choices and dataset-input labels.
Raw Parquet therefore appears beside ClickHouse without controller or React switches on adapter identity.

## Verification evidence

- `dotnet test Colossus.slnx --no-restore`: 181 passed.
- Parquet contract fixtures cover local and partitioned inputs, points, quadkeys, multipart WKT with holes,
  numeric coercion, null dictionaries, schema/extraction parity, provider-root confinement, fingerprint drift,
  metadata scan ceilings, and active cancellation.
- A real HTTP range server verifies remote Parquet metadata and bounded preview through DuckDB `httpfs`.
- DuckDB `EXPLAIN` evidence verifies projected columns and predicate pushdown without the unmapped payload.
- The same representative rows extracted from Parquet and ClickHouse produce byte-identical Arrow tiles.
- The generic source API onboards, validates, discovers, and previews a configured Parquet provider.
- `npm run lint`: passed.
- `npm test`: 23 files and 204 tests passed.
- `npm run build`: passed; executable bundle check reports 372.8 KiB initial JavaScript and a route-lazy viewer.
- `npm run test:storybook`: 4 files and 8 light/dark interaction and accessibility tests passed.
- `npm run test:visual`: 4 light/dark visual baselines passed.
- `npm run build:storybook`: passed.
- `npm audit --audit-level=high`: zero vulnerabilities.
- Live browser verification confirmed two adapters and the descriptor-driven Raw Parquet provider selector in
  the Vodafone-themed source workspace without persisting test catalog data.
