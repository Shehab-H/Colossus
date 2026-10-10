# 02a — MySQL database and appsettings configuration

- **Status:** Complete
- **Completed:** 2026-08-10
- **Depends on:** Item 02

## Motivation

The first durable-database implementation selected embedded SQLite and retained several environment-variable
overrides for source, storage, and rendering settings. The approved deployment direction instead uses the
already established Docker stack and treats appsettings as the application configuration source.

## Integration surface

- Replace the Infrastructure database adapter without changing the six Application repository ports.
- Put the application database connection string in `ConnectionStrings:Database`.
- Put tiles, staging, legacy views, and operator media locations in the shared `Storage` section.
- Bind ClickHouse, fold routing, render packing, and server URLs only from appsettings at runtime.
- Add MySQL to the existing Docker Compose stack and keep catalog integration tests isolated.

## Main risks

- MySQL constraint and GUID materialization behavior differs from SQLite.
- Tests backed by one shared schema would interfere when xUnit runs classes concurrently.
- Resolving storage paths differently in the server and bake host would split the published artifact tree.

## Final outcome

`MySqlDatabase` now implements the existing provider-independent repositories through MySqlConnector.
It creates a versioned InnoDB schema with workspace slug uniqueness, foreign keys, composite release/run
ownership, retention checks, and optimistic revisions. Each integration test creates and drops its own MySQL
database, so persistence and HTTP tests remain deterministic.

The server, bake host, and companion benchmark bind filesystem locations from `Storage`; the database uses the
standard `ConnectionStrings` section. ClickHouse credentials, server URLs, fold routing, and tile-pack opt-in
also live in appsettings. Runtime `COLOSSUS_*` overrides were removed. Relative storage values resolve from the
repository root in development and the process root in a standalone deployment.

The Docker Compose stack now owns a persistent `mysql:8.4` service on port 3306. The development appsettings
connection string and Compose database/user/password agree, and MySQL startup health is checked before tests.

## Acceptance evidence

- `dotnet build Colossus.slnx --no-restore` completed with zero warnings, and `dotnet test
  Colossus.slnx --no-build` passed all 163 backend tests.
- Six focused catalog/API tests passed against real isolated MySQL databases, including restart persistence,
  relationship constraints, optimistic concurrency, legacy import, and secret-safe HTTP responses.
- Source inspection finds no runtime environment-variable reads in `src/` or `tools/`; the only remaining
  `COLOSSUS_*` switch is test-only fixture regeneration.
- `dotnet format Colossus.slnx --verify-no-changes --no-restore` passed, and the transitive NuGet vulnerability
  scan reported no known vulnerable package in any project.
- The frontend retained its full baseline: 18 files and 184 tests passed; lint and the production build passed.
- Docker Compose validated, `colossus-mysql` reported healthy, and the live Debug server created schema version
  1, imported one connection, six datasets, and six maps, then returned all six from `/api/views`.
