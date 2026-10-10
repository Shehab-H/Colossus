# 09 — Release publication and retention

- **Status:** Complete
- **Completed:** 2026-08-20
- **Estimate:** 5–8 engineering days
- **Depends on:** Items 02 and 08

## Motivation

Scheduled jobs made map-keyed staging and publication from inside the bake unsafe. Concurrent attempts could
share extracts, catalog state was written after the current pointer moved, and cleanup had no durable notion
of current, pinned, active, or expired artifacts.

## Delivered outcome

- Every scheduled or ad-hoc attempt receives a unique workspace and release version. Extracts, grouped marks,
  and release preparation never share a map-keyed partial path.
- The bake writes to a private workspace, runs the complete row/group fidelity witness against those exact
  bytes, hashes the manifest, and only then makes the immutable version addressable.
- Scheduled publication is now an idempotent `Prepared → pointer flip → Published` workflow. A pointer failure
  or catalog failure after the flip resumes from the prepared release without repeating extraction/reduction.
- Release catalog records persist manifest hash, resolved source snapshot, source rows, tile count, artifact
  bytes, preparation/publication/verification timestamps, verification result, attempt id, pin, and deletion
  state. Schema version 4 preserves older releases as `Legacy / unverified` rather than inventing evidence.
- Retention supports a safe minimum `latest N`, an optional age window, or both. Current, pinned, active-run,
  count-window, age-window, and preparation-grace reasons are explicit; deleted and repeated cleanup are
  idempotent. Failed publication can be completed by reconciliation after storage/catalog recovery.
- Abandoned workspaces and incomplete filesystem publication prefixes are scavenged only after a configurable
  grace period and never while their run is active.
- [`IReleaseArtifactStore`](../../../../src/Colossus.Application/Abstractions/Baking/IBakeStore.cs) is the shared
  publication contract. The filesystem implementation uses a same-volume hidden prefix and atomic final move;
  the S3-compatible implementation uploads immutable objects with `manifest.json` last and performs a separate
  final pointer put without relying on rename semantics.
- The management API and UI expose pin/unpin and repair/retention actions plus verification, row, tile, size,
  hash, and publication metadata. Only `latest.json` receives the short cache policy; immutable version
  manifests receive immutable caching.

## Acceptance evidence

- Backend suite: **225/225 passed** on 2026-08-20.
- Frontend suite: **207/207 passed**; lint, TypeScript production build, and the 373.2 KiB route-lazy initial
  bundle contract passed.
- [`PreparedReleaseVerifierTests`](../../../../tests/Colossus.Tests/PreparedReleaseVerifierTests.cs) prove a
  tile-row or staged-extract mismatch is rejected before publication.
- [`BakeSchedulingTests`](../../../../tests/Colossus.Tests/BakeSchedulingTests.cs) inject pointer failure and
  post-pointer catalog failure; both finish from one prepared bake with two idempotent pointer attempts.
- [`FileBakeStoreTests`](../../../../tests/Colossus.Tests/FileBakeStoreTests.cs) prove private preparation,
  addressability before pointer mutation, unique concurrent workspaces, invalid-preparation isolation,
  idempotence, and current-release deletion protection.
- [`ReleaseMaintenanceTests`](../../../../tests/Colossus.Tests/ReleaseMaintenanceTests.cs) cover latest count,
  age, current, pin, active run, preparation grace, abandoned workspace, already-deleted repetition, and failed
  publication repair.
- [`ReleaseArtifactStoreContractTests`](../../../../tests/Colossus.Tests/ReleaseArtifactStoreContractTests.cs)
  run equivalent filesystem and object-store publication/current/deletion contracts and verify immutable versus
  pointer cache policies.
- [`CatalogApiTests`](../../../../tests/Colossus.Tests/CatalogApiTests.cs) exercise durable pin/unpin and delete an
  expired non-current release while proving the current pointer remains unchanged.
- Live browser smoke rendered a four-release map, showed the catalog/current release consistently, exercised
  pin then unpin on the oldest release, preserved the current release, and produced no browser warnings/errors.
- The refreshed live server remained hard-capped at 2 CPUs / 2 GiB / no swap and returned HTTP 200 for the API
  and OpenAPI document after the schema migration.

## Residual operations note

Real S3 credentials/endpoints were intentionally not committed. The S3-compatible adapter is contract-tested
through its object-client seam; item 10 owns live artifact-store readiness, outage drills, secret-safe production
configuration, health checks, CI, backup/reconciliation procedures, and the full deployment release checklist.
