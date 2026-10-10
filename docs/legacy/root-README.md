# Colossus

Render very large datasets (10M–1000M+ rows) with **no aggregation or simplification** of raw marks — on a map or in any chart — over a single engine:

```
source adapter (ClickHouse, Trino, or raw Parquet) → bake → Arrow IPC LOD tiles → static immutable serve → deck.gl binary attributes → GPU
```

A chart type is not a pipeline; it's `(mark + channel mapping) × reduction primitive`. Start at the
[documentation index](docs/README.md) for active work and the preserved historical reference set.

## Layout

| Path | What |
|------|------|
| `src/Colossus.Domain` | Domain models: catalog records, view snapshots, manifests, and tile math |
| `src/Colossus.Application` | Use cases and ports: catalog, bake orchestration, source/reduction seams, fidelity verification |
| `src/Colossus.Infrastructure` | Adapters: MySQL catalog, ClickHouse/Trino/raw-Parquet sources, DuckDB reducers, Arrow tiles, and file storage |
| `src/Colossus.Bake` | Developer/repair CLI: bake registered views / `verify` |
| `src/Colossus.Server` | API and worker host: product catalog, persistent bake scheduling, compatibility view API, and static tiles |
| `tests/Colossus.Tests` | Unit, architecture, MySQL/scheduling integration, and API tests |
| `web/` | Vite + React + deck.gl + MapLibre; config-driven renderer |
| `views/` | Legacy view configs imported idempotently into the product catalog |
| `docker/` | Local MySQL application database, ClickHouse, pinned Trino with an isolated PostgreSQL Iceberg test catalog, and Parquet-backed integration services |

## Dev quickstart

Colossus reads from your own ClickHouse or Trino tables and server-configured Parquet providers—there is no bundled
data generator. Configure a source in the `/sources` workspace or point a legacy view at your data using the
archived canonical [view configuration reference](docs/archive/2026/2026-08-10-pre-index/VIEW_CONFIG.md), then:

```bash
# 1. MySQL application/catalog/job store + ClickHouse + Trino/Iceberg integration sources
docker compose -f docker/docker-compose.yml up -d

# 2. Serve the API, persistent bake worker, and tiles (Swagger UI at /swagger)
dotnet run --project src/Colossus.Server

# 3. Frontend — create/manage maps and use Run now from /maps
cd web && npm run dev

# Optional repair path
dotnet run --project src/Colossus.Bake -- <view-id>
```

Runtime settings live in each host's `appsettings.json`. `ConnectionStrings:Database` selects the single
MySQL application database used by both the product catalog and Hangfire's prefixed infrastructure tables; `Scheduling` and
`BakeExecution` own worker and run-coordination limits; `Storage` owns local work, tile, legacy-view, and media locations;
`ReleaseArtifacts` selects filesystem or S3-compatible immutable publication; `ReleaseMaintenance` owns the
abandoned-workspace grace period; `ClickHouse` owns its source
credentials; `Trino` owns coordinator defaults, bounded protocol limits, work storage, and administrator-owned
header profiles; and `ParquetSources` declares administrator-owned local/HTTP/S3-compatible provider aliases.
The checked-in `local` Parquet provider is confined to `datasets/parquet`, while the checked-in ClickHouse
values match the Docker Compose service. The PostgreSQL service in Compose belongs only to the local Trino/Iceberg
integration fixture; the Colossus application does not connect to it.

### Bake hardware envelope

`BakeResources` applies from the first source probe through extraction, reduction, packing, and compression.
Zero keeps the engine defaults; set both values to trade elapsed time and staging-disk I/O for a smaller host
footprint (`RamMb` must be at least 256 when enabled):

```json
"BakeResources": {
  "CpuCores": 2,
  "RamMb": 4096
}
```

This is an out-of-core throughput envelope, not an "exceeded budget" failure rule. It reduces ClickHouse and
Trino source-query parallelism, bounds local DuckDB threads, streams source results with backpressure, spills
analytical work to the configured staging directories, derives smaller Arrow tiles from the extracted row
footprint, bounds codec dictionary samples, and streams Brotli rather than duplicating whole tiles in RAM.
When an envelope is active the scheduler runs one bake at a time so concurrent workers cannot each consume the
full allowance. A smaller value therefore means a longer bake and more temporary disk use. Restart the server
or CLI host after changing it, and provision staging space for spill files.

`BakeResources` controls algorithms; the OS must own the hard process ceiling because Arrow, DuckDB native
allocations, and filesystem cache are not all .NET heap. The checked-in Compose stack therefore applies CPU,
RAM, and no-swap cgroup limits to MySQL, ClickHouse, Trino, and its PostgreSQL catalog. Override them with
`COLOSSUS_<SERVICE>_CPUS` and `COLOSSUS_<SERVICE>_MEMORY_LIMIT` before `docker compose up`. Apply the same
pattern to the server container or systemd service, leaving headroom above `BakeResources:RamMb`; the verified
local stress pairing is a 1,536 MB bake envelope inside a 2 GiB/2-CPU hard application limit.

Run the tests with `dotnet test`; verify a bake's fidelity invariant with
`dotnet run --project src/Colossus.Bake -- verify`.

### Release publication and retention

Every attempt writes into `Storage:StagingDirectory/workspaces/<attempt>`, runs the full row/fact fidelity
witness there, records a SHA-256 manifest hash and resolved source snapshot, and makes an immutable version
addressable before the small current pointer moves. Scheduled runs persist `Prepared` before the pointer flip,
so pointer or catalog interruption resumes without rebaking. The management release list exposes verification
evidence, pin/unpin, and an idempotent retention/repair action.

Filesystem publication is the default. To publish immutable versions and the final pointer to S3, R2, MinIO,
or another S3-compatible service, configure the provider with environment variables (keep credentials out of
appsettings and Git):

```ini
ReleaseArtifacts__Provider=s3
ReleaseArtifacts__Bucket=colossus-releases
ReleaseArtifacts__Prefix=tiles
ReleaseArtifacts__Region=us-east-1
# For R2/MinIO or another compatible endpoint:
ReleaseArtifacts__ServiceUrl=https://OBJECT-ENDPOINT
ReleaseArtifacts__ForcePathStyle=true
ReleaseArtifacts__AccessKey=REPLACE_ME
ReleaseArtifacts__SecretKey=REPLACE_ME
ReleaseMaintenance__WorkspaceGracePeriod=1.00:00:00
```

If explicit keys are omitted, the AWS SDK credential chain is used. Object uploads write every immutable
artifact before `manifest.json`; `latest.json` is a separate final put with a short cache policy. Retention
keeps at least one release and protects the current version, pins, active jobs, latest-count and age windows.

For a production-style Ubuntu deployment, follow the
[server deployment checklist](docs/sagas/productized-map-application/10-operational-and-quality-gates.md#server-deployment-checklist-ubuntu--nginx--systemd).

## Embedding maps

Every map is fully described by its URL, so an `<iframe>` reproduces it exactly. Add `embed=1` for a
chromeless frame (map + legend only), and pin the rest with query params:

```
/?embed=1&view=ookla-fixed&color=download_mbps&scale=quantize&bins=6&theme=light&lng=10&lat=50&z=3.6
```

`view`, `color`, `scale` (`linear`/`log`/`sqrt`/`diverging`/`quantize`/`quantile`/`threshold`/…),
`bins`, `midpoint`, `scheme`, `reverse`, `theme` (`dark`/`light`), camera `lng`/`lat`/`z`, and coarse
`f_<dim>=<value>` filters. The app's HUD has an **Embed** button that copies the `<iframe>` snippet for
the current view state. [web/public/embed.html](web/public/embed.html) is a raw HTML/CSS/JS gallery that
embeds one baked table many ways — the design's stress test; open it at `/embed.html`.

## Status

The batch engine works end-to-end: planner-chosen reduction, Arrow IPC LOD tiles, GPU filtering and color,
click-to-inspect, a dark/light basemap toggle, group/measure folding, packed tile transfer, and
URL-addressable embeddable maps. Current work is indexed in [docs/README.md](docs/README.md).
