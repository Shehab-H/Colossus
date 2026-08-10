# 09 — Release publication and retention

- **Status:** Candidate
- **Estimate:** 5–8 engineering days
- **Depends on:** Items 02 and 08

## Motivation

The existing file store has a strong immutable-version and atomic-pointer basis, but staging is keyed only
by view id, cleanup is manual, and product state does not record publication or retention. Scheduled jobs
make isolation and deterministic disposal mandatory.

## Goal

Model each successful bake as an immutable release, publish it atomically, and remove expired releases
without risking the current version, an active run, or a concurrent reader.

## Integration surface

- [`IBakeStore`](../../../src/Colossus.Application/Abstractions/Baking/IBakeStore.cs) and
  [`FileBakeStore`](../../../src/Colossus.Infrastructure/Baking/FileBakeStore.cs), evolved behind item 01's
  application-owned artifact ports.
- BakeRun/Release catalog records from item 02 and the Hangfire coordinator from item 08.
- Manifest and latest-pointer loading in the server and web viewer.
- Filesystem serving today and the S3-compatible deployment path recorded in the historical deployment
  guide.

## Required behavior

- Use a unique run/staging directory and release identifier for every attempt.
- Write artifacts to a non-public temporary prefix, verify them, then make the release addressable and flip
  the current pointer as the final publication step.
- Record manifest hash, source snapshot, row/tile counts, byte size, timestamps, and verification result.
- Support retention by `keep latest N`, age, or both, with a safe minimum of one published release.
- Run retention after successful publication and as a repair/reconciliation job.
- Protect the current release, explicitly pinned releases, and artifacts owned by active jobs.
- Remove abandoned temporary runs after a grace period.
- Keep filesystem and S3-compatible/object-storage implementations behind one artifact-store contract.
- Make cleanup idempotent and report every retained/deleted/skipped reason.

## Main risks

- Deleting the old release before the new pointer is globally visible can create an outage.
- Object stores do not provide filesystem rename semantics; publication must rely on immutable prefixes and
  a final small pointer/catalog update.
- CDN caching means a mutable pointer needs an explicit short cache policy while release artifacts remain
  immutable.
- Cleanup racing a fold request or active reader needs an agreed grace/pinning policy.

## Acceptance evidence required

- Failure injection at every publication stage proves the previous release remains usable.
- Concurrent runs cannot share staging or overwrite one another.
- Retention tests cover latest-N, age, pinned, current, active, abandoned, and repeated cleanup cases.
- Filesystem and object-store contract tests produce equivalent release metadata and current resolution.
- A scheduled multi-version browser smoke test retains the current map throughout publication and cleanup.
