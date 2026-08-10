# 08 — Hangfire bake scheduling

- **Status:** Candidate
- **Estimate:** 5–8 engineering days
- **Depends on:** Items 02 and 05

## Motivation

Production bakes currently start from [`Colossus.Bake/Program.cs`](../../../src/Colossus.Bake/Program.cs)
and run synchronously. Operators need recurring refresh, run-now, durable history, progress, cancellation,
retry, and restart recovery without shell access.

## Goal

Use Hangfire with persistent storage to execute application-level bake jobs from saved map snapshots and
bake policies. Hangfire is scheduling/execution infrastructure; no Hangfire type may enter Domain or the
core bake use case.

## Integration surface

- [`BakeViewUseCase`](../../../src/Colossus.Application/BakeViewUseCase.cs), wrapped by an application run
  coordinator that reports progress and publication outcomes.
- Catalog BakePolicy/BakeRun records from item 02.
- [`Colossus.Server`](../../../src/Colossus.Server) as the initial Hangfire host and composition root, while
  retaining the option of a separate worker host.
- Management actions and run history in item 04.

## Required behavior

- Configure an interval/recurrence and time zone per map; enable or disable it without deleting history.
- Reconcile saved policies to Hangfire recurring jobs on startup and after edits.
- Provide a management-page `Run now` action.
- Capture an immutable map/dataset/connection snapshot when a run starts.
- Enforce one active bake per map with a persistent lock or equivalent non-overlap rule.
- Report stages: queued, probing, extracting, tiling, verifying, publishing, cleanup, succeeded, failed,
  and cancelled.
- Persist timestamps, progress, row/tile counts, artifact size, release id, and structured failure details.
- Coalesce missed recurring intervals after downtime rather than replaying an unbounded backlog.
- Apply bounded automatic retry only to classified transient failures.

The current CLI may remain temporarily as a developer/repair tool, but the application workflow and normal
deployment must not depend on it. Remove or demote it only after run-now and scheduled parity are verified.

## Main risks

- Long bakes can outlive a web request or app recycle; jobs must own their scope and cancellation token.
- A Hangfire retry can publish twice unless run and release identifiers are idempotent.
- Progress updates can overload persistence if emitted per tile instead of at bounded intervals.
- Hosting the worker in the web process is simple but constrains independent scaling; application contracts
  must permit a separate worker host later.

## Acceptance evidence required

- Persistent Hangfire storage survives a server restart with schedules and job history intact.
- Integration tests cover run-now, recurrence reconciliation, non-overlap, cancellation, transient retry,
  permanent failure, and restart recovery.
- Controllers enqueue application commands and contain no bake algorithm or source logic.
- No failed/cancelled run changes the current release pointer.
- Browser job history shows live bounded progress and actionable failure information.
