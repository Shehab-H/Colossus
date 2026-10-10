# 08 — Hangfire bake scheduling

- **Status:** Complete (2026-08-14)
- **Estimate:** 5–8 engineering days
- **Depends on:** Items 02 and 05

## Motivation

Production bakes previously started from [`Colossus.Bake/Program.cs`](../../../../src/Colossus.Bake/Program.cs)
and run synchronously. Operators need recurring refresh, run-now, durable history, progress, cancellation,
retry, and restart recovery without shell access.

## Goal

Use Hangfire with persistent storage to execute application-level bake jobs from saved map snapshots and
bake policies. Hangfire is scheduling/execution infrastructure; no Hangfire type may enter Domain or the
core bake use case.

## Integration surface

- [`BakeViewUseCase`](../../../../src/Colossus.Application/BakeViewUseCase.cs), wrapped by an application run
  coordinator that reports progress and publication outcomes.
- Catalog BakePolicy/BakeRun records from item 02.
- [`Colossus.Server`](../../../../src/Colossus.Server) as the initial Hangfire host and composition root, while
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

## Outcome

The normal application path now owns bake scheduling through provider-independent application ports.
[`BakeSchedulingService`](../../../../src/Colossus.Application/Catalog/BakeSchedulingService.cs) captures an
immutable catalog snapshot before enqueue, reconciles saved policies, coalesces missed fixed intervals, attaches
durable job identifiers, recovers only the narrow capture-before-enqueue orphan case, and coordinates run-now and
cancellation. [`BakeRunExecutor`](../../../../src/Colossus.Application/Catalog/BakeRunExecutor.cs) owns all
durable state transitions, bounded progress writes, application-level transient retry, cancellation polling,
release metrics, and terminal outcomes. Domain and Application contain no Hangfire types.

The server adapter uses Hangfire 1.8.24 with Hangfire.MySqlStorage 2.0.3. It shares the application's single
MySQL database through `ConnectionStrings:Database`, while a `Hangfire` table prefix keeps scheduler-owned
tables separate from the product catalog. Dapper is pinned directly to a current release so the provider's old
transitive minimum does not introduce obsolete SQL Server binaries. Startup reconciliation maps every enabled policy to a persistent
minutely trigger; the application checks the saved interval and last run so downtime creates at most one due run.
Worker cancellation tokens and catalog cancellation requests converge on the same executor. Hangfire's broad
automatic retry is disabled because only source failures classified as transient may retry.

MySQL run records now persist queued/probing/extracting/tiling/verifying/publishing/cleanup and terminal stages,
bounded progress, status messages, timestamps, attempts, cancellation state, structured failure category/detail,
row and tile counts, output bytes, release ID, and background-job ID. Connection-scoped MySQL advisory locks
provide distributed per-map exclusion and release automatically when a process exits. Release creation is
idempotent per run. The CLI remains available as an explicit developer and repair path.

The Vodafone-themed management detail page polls only while a run is active, shows live stage and percentage,
supports cancellation, renders actionable failure details, and displays attempt, row, tile, artifact-size, and
release history without adding raw job infrastructure to the frontend.

## Verification evidence

- `dotnet test Colossus.slnx`: 199 passed, zero skipped. The scheduling suite covers immutable run-now
  capture, policy reconciliation, due-run coalescing, durable active cancellation, transient-only bounded retry,
  permanent failure, successful metrics/release recording, overlap rejection, and real MySQL advisory-lock
  exclusion/release. A host-level integration test also proves catalog and prefixed Hangfire tables are created
  in the same fresh MySQL database without a second connection string.
- `dotnet list Colossus.slnx package --vulnerable --include-transitive`: no vulnerable packages; the server
  pins Dapper 2.1.79 and Newtonsoft.Json 13.0.4 above the legacy transitive minima exposed by the job stack.
- Existing restart-persistence coverage passes against a fresh MySQL database with the schema-v2 run model.
- `docker compose config --quiet`: passed without a dedicated Hangfire database service.
- Live MySQL inspection confirmed the prefixed Hangfire schema, recurring policy, and job history remained after
  an API process restart. The application uses no PostgreSQL connection; Compose's remaining PostgreSQL service
  is isolated to the local Trino/Iceberg integration fixture.
- Live run-now verification baked the 100-row `scheduler-demo` ClickHouse map to one tile and a 6,158-byte
  published release with a durable release ID. A separate long-running bake accepted cancellation, ended
  `cancelled`, and published no release; overlapping submissions did not execute.
- Browser verification at the management detail route confirmed the enabled recurrence and next-run time,
  published preview, successful 100% run, 100-row/one-tile/6.2-KB metrics, attempt count, and release artifact.
- `npm run lint`: passed.
- `npm test`: 23 files and 204 tests passed.
- `npm run build`: passed; the executable bundle check reports 372.9 KiB initial JavaScript and a route-lazy
  viewer.
- `npm run test:storybook`: 4 files and 8 light/dark interaction and accessibility tests passed.
- `npm run test:visual`: 4 light/dark visual baselines passed.
- `npm run build:storybook`: passed.
- `npm audit --audit-level=high`: zero vulnerabilities.
