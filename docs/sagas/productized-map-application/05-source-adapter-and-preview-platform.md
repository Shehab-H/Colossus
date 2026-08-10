# 05 — Source adapter and preview platform

- **Status:** Candidate
- **Estimate:** 4–7 engineering days
- **Depends on:** Items 01 and 02

## Motivation

[`ISourceAdapter`](../../../src/Colossus.Domain/Sources/ISourceAdapter.cs) currently exposes only probe and
extract against a complete `ViewConfig`, and [`SourceAdapterCatalog`](../../../src/Colossus.Infrastructure/Sources/SourceAdapterCatalog.cs)
constructs ClickHouse through a switch. Product onboarding also needs connection testing, schema discovery,
sample rows, geometry preview, capabilities, progress, and structured errors.

## Goal

Define one adapter platform that supports ClickHouse, Trino, and Parquet without leaking their clients or
dialects into map, bake, controller, or UI code.

## Integration surface

- [`ISourceAdapter`](../../../src/Colossus.Domain/Sources/ISourceAdapter.cs) and
  [`ISourceAdapterCatalog`](../../../src/Colossus.Domain/Sources/ISourceAdapterCatalog.cs), moved or adapted
  according to item 01's dependency rules.
- [`ClickHouseAdapter`](../../../src/Colossus.Infrastructure/ClickHouse/ClickHouseAdapter.cs) as the first
  production implementation of the expanded contract.
- Catalog Connection/Dataset records from item 02 and the map editor from item 04.
- [`BakeViewUseCase`](../../../src/Colossus.Application/BakeViewUseCase.cs) and its staging boundary.

## Required adapter capabilities

- Stable adapter identifier and capability descriptor.
- Validate connection/location.
- Discover tables or locations where meaningful.
- Inspect query/dataset schema and fetch bounded sample rows.
- Probe row count, distinct geometries, and bounds.
- Extract a canonical spatial staging Parquet with progress and cancellation.
- Classify errors as configuration, connectivity, query, type, cancellation, or transient failure.

The output contract—not SQL—is shared: representative `x/y`, required geometry payload for area marks,
declared typed channels, deterministic null handling, and a spatially suitable order. An adapter may push
normalization into its source engine or use a shared DuckDB staging normalizer, but both paths must pass the
same contract fixtures.

## Preview behavior

- Strict row, byte, and duration budgets.
- Schema and sample retrieval never launch a production bake.
- Geometry preview catches swapped coordinates, invalid WKT, unsupported types, and empty bounds.
- Preview uses the same mapping and type rules as the later bake.
- Cancellation reaches the underlying source request.

## Main risks

- Designing the contract around ClickHouse SQL would make Trino and Parquet second-class adapters.
- Returning unbounded samples or buffering a result set defeats the purpose of the platform.
- Preview/bake rule drift creates configurations that preview successfully and fail in production.

## Acceptance evidence required

- Adapter contract tests run against ClickHouse and a deterministic fake adapter before new adapters land.
- Invalid mappings return structured, field-addressable errors.
- Progress and cancellation propagate through the application use case.
- A preview and full extraction of the same fixture produce identical types and geometry semantics.
- No controller, Hangfire job, or React component switches on an adapter identifier.
