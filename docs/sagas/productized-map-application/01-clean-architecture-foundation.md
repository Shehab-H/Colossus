# 01 — Clean architecture foundation

- **Status:** Active
- **Estimate:** 4–6 engineering days

## Motivation

The repository already has Domain, Application, Infrastructure, and host projects, but several boundaries
are only conventional. Application use cases still read file metadata and files directly, source adapters
are constructed through a switch, mutable map state is represented by a file registry, and the frontend
root composes viewer behavior with product controls. Adding two sources, durable jobs, and release retention
without first tightening these seams would spread source, storage, and scheduler conditionals across the
codebase.

## Goal

Establish and enforce an incremental clean-architecture baseline before product features land:

- **Domain** owns pure map, dataset, geometry, channel, bake-policy, and release invariants.
- **Application** owns use cases and ports. It must not depend on ASP.NET, Hangfire, concrete databases,
  the environment, the console, or direct filesystem I/O.
- **Infrastructure** implements source, persistence, artifact, clock, and scheduling ports.
- **Hosts** own configuration, dependency injection, transports, and process lifetime.
- **Web features** separate reusable viewer state/rendering from management routes and API clients.

## Integration surface

- [`BakeViewUseCase`](../../../src/Colossus.Application/BakeViewUseCase.cs) and
  [`VerifyFidelityUseCase`](../../../src/Colossus.Application/VerifyFidelityUseCase.cs).
- Ports currently under [`Colossus.Domain/Baking`](../../../src/Colossus.Domain/Baking) and
  [`Colossus.Domain/Sources`](../../../src/Colossus.Domain/Sources).
- [`ServiceCollectionExtensions`](../../../src/Colossus.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs)
  and both current hosts.
- [`App.tsx`](../../../web/src/App.tsx), its hooks, and the viewer-facing components.

## Implementation boundaries

1. Add characterization tests for the existing ClickHouse bake orchestration, manifest publication,
   fidelity verification, registry behavior, and viewer boot URL.
2. Record the allowed project dependency graph and enforce it with architecture tests.
3. Put use-case ports with the application boundary; keep domain types free of persistence semantics.
4. Replace `File`, `FileInfo`, `Directory`, environment, and console access inside Application with narrow
   ports or returned progress/result events.
5. Replace manually constructed adapter switches with dependency-injected adapter registration keyed by a
   stable adapter identifier.
6. Split frontend folders into viewer, management, shared domain, and API infrastructure without changing
   pixels or routes yet.
7. Delete dead scaffold assets and obsolete code only after reference searches and tests prove they are
   unused. Do not mix behavior changes into mechanical moves.

This item does not introduce a database, Hangfire, Trino, or Parquet. It prepares stable seams for those
items and leaves compatibility adapters for the current view JSON and CLI host until their replacements
are verified.

## Main risks

- A broad namespace/folder rewrite can consume time without improving boundaries. Every move must remove a
  demonstrated dependency violation or enable a listed product item.
- Moving ports can create circular references if transport and domain models are not separated first.
- Characterization tests must pin behavior rather than private implementation structure.
- Frontend extraction can accidentally change deck layer identity and regress staged GPU admission.

## Acceptance evidence required

- An architecture decision in this file is reflected by project references and automated dependency tests.
- Domain and Application contain no direct filesystem, console, environment, ASP.NET, Hangfire, or concrete
  source-client calls.
- Existing ClickHouse bake output and manifest serialization remain fixture-compatible.
- Current backend tests, frontend tests, lint, and production build pass.
- Browser parity covers a dense polygon map, a point map, filters, picking, and the existing embed path.
- A final dependency inventory lists any intentional exception with an owner and removal item.
