# Deferred 10 — Operational and quality gates

- **Status:** Deferred
- **Deferred:** 2026-08-20 by operator decision
- **Estimate:** 3–5 engineering days
- **Depends on:** Items 03–09

## Disposition

This broad roadmap item is intentionally not scheduled. It is preserved as historical reference, not as an
active backlog or an implied implementation sequence. Further operational hardening should be scoped from a
specific observed failure, risk, resource-limit result, or operator need and verified against the existing
design before another increment is chosen.

## Motivation

A scheduled data application needs repeatable builds, visible failure states, resource guardrails, and
recovery procedures. The repository currently has strong unit suites but no active CI workflow and relies
on manual deployment and smoke testing.

## Goal

Make the single-operator product diagnosable and safe to operate without introducing authentication or a
full observability platform.

## Integration surface

- New workflows under [`.github/workflows`](../../../../.github/workflows) and the existing backend/frontend
  test commands.
- Server health, logging, and diagnostics configuration in
  [`Colossus.Server`](../../../../src/Colossus.Server).
- Catalog, Hangfire storage, source adapters, and artifact-store health boundaries from earlier items.
- Job/release operational actions on the management pages from item 04.

## Required behavior

- CI runs backend build/tests, architecture tests, frontend tests, lint, type-check/build, and Markdown-link
  validation on every change.
- Health endpoints distinguish process liveness, catalog readiness, Hangfire storage readiness, and
  artifact-store readiness without executing a source query.
- Structured logs carry map id, run id, release id, source adapter, stage, duration, and error category.
- Job metrics expose queue latency, stage duration, success/failure/cancellation, rows, tiles, and bytes.
- Preflight checks cover writable staging, artifact-store access, and configurable disk-space headroom.
- Operators can retry, cancel, inspect failure details, and reconcile abandoned runs/releases from the UI.
- Production defaults hide Swagger and the performance dashboard unless explicitly enabled.
- A documented backup/restore procedure covers catalog and Hangfire persistence; immutable releases can be
  reconstructed or reconciled from object storage.

## Main risks

- Health checks that query external datasets can make the app unhealthy whenever a source is unavailable.
- Logging raw SQL, row samples, headers, or connection settings can leak source data and credentials even in
  a single-user deployment.
- High-cardinality per-tile metrics can overload the chosen metrics sink.

## Acceptance evidence required

- CI is green from a clean checkout and blocks a deliberate dependency-rule or link failure.
- Restart, unavailable source, unavailable artifact store, low disk, cancelled bake, and failed publication
  drills produce distinct actionable states.
- Logs contain correlation identifiers and exclude configured secrets and sample-row contents.
- A release checklist verifies map management, all three adapters, scheduling, retention, full viewer,
  embed viewer, fidelity, and dense interaction performance.

## Server deployment checklist (Ubuntu + Nginx + systemd)

This is the current single-host deployment path for an x64 Ubuntu server. It serves the built React app,
API, and local tile tree from one Colossus process behind Nginx. MySQL remains durable and separate; source
data may live in ClickHouse, Trino, or an administrator-owned Parquet directory. For background on the host
pattern, see Microsoft's [.NET 10 Ubuntu installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install)
and [ASP.NET Core with Nginx and systemd guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0).

The checklist intentionally does not claim item 10 is complete. Colossus currently has no authentication,
dedicated health endpoint, production Swagger gate, or automated backup job. Keep the management/API surface
private or protect the whole site at Nginx until those gates land.

### 1. Provision the host

- [ ] Create a non-root operator with `sudo`, an SSH key, and automatic security updates.
- [ ] Point the production DNS name at the server.
- [ ] Allow inbound SSH, HTTP, and HTTPS only. Do not expose MySQL `3306`, ClickHouse `8123/9000`, or Trino
  `8080` publicly.
- [ ] Provision enough disk for the source extract, DuckDB spill files, immutable tile releases, MySQL,
  backups, and at least one rollback release. A lower RAM envelope intentionally increases staging I/O.
- [ ] Install `git`, Nginx, a TLS client such as Certbot, Docker Engine with Compose if containers are used,
  Node.js 24 with npm, and the .NET 10 SDK. The final publish is self-contained, so only the build host needs
  Node and the SDK.
- [ ] Create a dedicated service account and durable directories:

  ```bash
  sudo useradd --system --home /opt/colossus --shell /usr/sbin/nologin colossus
  sudo mkdir -p /opt/colossus/releases /var/lib/colossus/{tiles,staging,media,datasets/parquet}
  sudo chown -R colossus:colossus /opt/colossus /var/lib/colossus
  ```

### 2. Provision MySQL and optional source engines

- [ ] Use MySQL 8.4, either managed, native, or the repository's Compose service. Create a `colossus`
  database and least-privilege application user. Use a unique production password.
- [ ] For a temporary single-host installation, start only MySQL from the repository stack:

  ```bash
  docker compose -f docker/docker-compose.yml up -d mysql
  docker compose -f docker/docker-compose.yml ps mysql
  ```

- [ ] Before treating that Compose service as production, replace every checked-in development password,
  keep port `3306` behind the host firewall/private network, and put `/var/lib/docker` on backed-up storage.
- [ ] Start ClickHouse and/or Trino only when those adapters are needed. They can instead be remote services;
  add their connections later in the `/sources` UI.
- [ ] Confirm the application host can reach MySQL and each selected source, while the public Internet cannot.

### 3. Build and verify a release

- [ ] Clone the GitHub repository and select the commit to deploy:

  ```bash
  git clone https://github.com/Shehab-H/Colossus.git
  cd Colossus
  git checkout main
  git pull --ff-only
  ```

- [ ] Restore and verify the backend:

  ```bash
  dotnet restore
  dotnet build Colossus.slnx -c Release --no-restore
  # Full integration suite: start the checked-in fixtures first when equivalent services are not remote.
  docker compose -f docker/docker-compose.yml up -d mysql clickhouse trino-catalog trino
  dotnet test tests/Colossus.Tests/Colossus.Tests.csproj -c Release --no-build
  ```

  Database and source integration tests require their corresponding services. Do not interpret connection
  failures from an absent MySQL/ClickHouse/Trino fixture as passing tests.

- [ ] Build the frontend against the same-origin production routes:

  ```bash
  cd web
  npm ci
  VITE_API_BASE=/api VITE_TILES_BASE=/tiles npm run test
  VITE_API_BASE=/api VITE_TILES_BASE=/tiles npm run lint
  VITE_API_BASE=/api VITE_TILES_BASE=/tiles npm run build
  cd ..
  rm -rf src/Colossus.Server/wwwroot
  mkdir -p src/Colossus.Server/wwwroot
  cp -a web/dist/. src/Colossus.Server/wwwroot/
  ```

- [ ] Publish a self-contained Linux release and include the legacy view imports:

  ```bash
  RELEASE_ID="$(date -u +%Y%m%dT%H%M%SZ)-$(git rev-parse --short HEAD)"
  dotnet publish src/Colossus.Server/Colossus.Server.csproj -c Release -r linux-x64 \
    --self-contained true -o "publish/$RELEASE_ID"
  cp -a views "publish/$RELEASE_ID/views"
  sudo mkdir -p "/opt/colossus/releases/$RELEASE_ID"
  sudo cp -a "publish/$RELEASE_ID/." "/opt/colossus/releases/$RELEASE_ID/"
  sudo chown -R colossus:colossus "/opt/colossus/releases/$RELEASE_ID"
  sudo ln -sfn "/opt/colossus/releases/$RELEASE_ID" /opt/colossus/current
  ```

### 4. Configure production without committing secrets

- [ ] Create the protected configuration directory, then create `/etc/colossus/colossus.env`. ASP.NET Core maps double
  underscores to configuration sections:

  ```bash
  sudo install -d -o root -g root -m 0755 /etc/colossus
  sudo install -o root -g root -m 0600 /dev/null /etc/colossus/colossus.env
  sudoedit /etc/colossus/colossus.env
  ```

  ```ini
  ASPNETCORE_ENVIRONMENT=Production
  ConnectionStrings__Database="Server=127.0.0.1;Port=3306;Database=colossus;User ID=colossus;Password=REPLACE_ME;Pooling=true;Allow User Variables=true"
  Server__ServerUrl=http://127.0.0.1:5174
  Server__WebBaseUrl=https://maps.example.com
  Storage__TilesDirectory=/var/lib/colossus/tiles
  Storage__StagingDirectory=/var/lib/colossus/staging
  Storage__LegacyViewsDirectory=/opt/colossus/current/views
  Storage__MediaDirectory=/var/lib/colossus/media
  ParquetSources__WorkDirectory=/var/lib/colossus/staging/parquet-sources
  ParquetSources__Providers__local__Kind=local
  ParquetSources__Providers__local__BaseLocation=/var/lib/colossus/datasets/parquet
  BakeResources__CpuCores=2
  BakeResources__RamMb=4096
  Scheduling__Enabled=true
  Scheduling__WorkerCount=1
  ```

- [ ] Set `ClickHouse__BaseUrl`, `ClickHouse__User`, and `ClickHouse__Password` only if the default/legacy
  ClickHouse connection is used. Connections created in the UI own their own adapter settings.
- [ ] Keep `BakeResources__RamMb` at `0` for engine defaults or at least `256` when enabled. With an envelope
  active, Colossus deliberately runs one bake at a time and trades time plus staging disk for lower CPU/RAM.
- [ ] Verify permissions:

  ```bash
  sudo chown root:root /etc/colossus/colossus.env
  sudo chmod 0600 /etc/colossus/colossus.env
  sudo -u colossus test -w /var/lib/colossus/staging
  sudo -u colossus test -w /var/lib/colossus/tiles
  ```

### 5. Install the systemd service

- [ ] Create `/etc/systemd/system/colossus.service`:

  ```ini
  [Unit]
  Description=Colossus map server and bake worker
  Wants=network-online.target
  After=network-online.target

  [Service]
  Type=simple
  User=colossus
  Group=colossus
  WorkingDirectory=/opt/colossus/current
  ExecStart=/opt/colossus/current/Colossus.Server
  EnvironmentFile=/etc/colossus/colossus.env
  Restart=always
  RestartSec=5
  KillSignal=SIGINT
  TimeoutStopSec=60
  NoNewPrivileges=true
  PrivateTmp=true
  ProtectSystem=strict
  ProtectHome=true
  ReadWritePaths=/var/lib/colossus

  [Install]
  WantedBy=multi-user.target
  ```

- [ ] Load, enable, and inspect the service:

  ```bash
  sudo systemctl daemon-reload
  sudo systemctl enable --now colossus
  sudo systemctl status colossus --no-pager
  sudo journalctl -u colossus -n 200 --no-pager
  curl --fail http://127.0.0.1:5174/api/source-adapters
  ```

### 6. Put Nginx and TLS in front

- [ ] Configure an exact `server_name` and proxy only to the loopback Kestrel endpoint:

  ```nginx
  server {
      listen 80;
      listen [::]:80;
      server_name maps.example.com;

      client_max_body_size 100m;

      location / {
          proxy_pass http://127.0.0.1:5174;
          proxy_http_version 1.1;
          proxy_set_header Host $host;
          proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
          proxy_set_header X-Forwarded-Proto $scheme;
          proxy_read_timeout 300s;
      }
  }
  ```

- [ ] Run `sudo nginx -t`, reload Nginx, obtain a certificate for the exact production hostname, enable
  automatic renewal, and verify HTTPS before closing the maintenance window.
- [ ] Because authentication is not implemented yet, add Nginx Basic Auth, an IP allowlist, VPN access, or
  an identity-aware proxy before exposing `/maps`, `/sources`, `/swagger`, and the write APIs.

### 7. Load data and run the first bake

- [ ] Copy local Parquet inputs under `/var/lib/colossus/datasets/parquet`, or configure ClickHouse/Trino
  connections from `https://maps.example.com/sources`.
- [ ] Confirm the relevant source validates and previews successfully before creating a map.
- [ ] Create or import a map, use **Run now**, and watch the durable bake history until it succeeds.
- [ ] Confirm that a lower hardware envelope slows the source/reduce/compress stages without cancelling the
  run, and monitor both staging free space and service memory during the first production-scale bake.

### 8. Smoke-test the live system

- [ ] `curl --fail https://maps.example.com/` returns the application shell.
- [ ] `curl --fail https://maps.example.com/api/source-adapters` returns the adapter catalog.
- [ ] `curl --fail https://maps.example.com/api/maps` returns the map catalog.
- [ ] The `/sources` UI validates one configured source and renders a bounded preview.
- [ ] The `/maps` UI creates/edits a map and queues a bake.
- [ ] A published map loads its manifest and tiles; browser network requests for `.arrow` succeed, and
  ranged `facts.pack` requests return `206` when a companion pack is used.
- [ ] Restarting `colossus.service` preserves MySQL catalog data, schedules, run history, media, and tiles.
- [ ] `sudo journalctl -u colossus --since today` contains no connection strings, passwords, raw source
  rows, or unexpected repeated failures.

### 9. Back up before calling the deployment complete

- [ ] Schedule encrypted off-host MySQL dumps and test restoring one into a disposable database.
- [ ] Back up `/var/lib/colossus/tiles` and `/var/lib/colossus/media`. Parquet inputs must be backed up when
  they are authoritative rather than reproducible from another source.
- [ ] Do not back up transient staging files as if they were releases; size and monitor the staging volume
  instead.
- [ ] Back up `/etc/colossus/colossus.env` in a secrets manager, not in Git.
- [ ] Record the deployed commit, database backup identifier, release directory, and smoke-test result.

### 10. Upgrade and rollback

- [ ] Build every upgrade into a new immutable `/opt/colossus/releases/<id>` directory; never overwrite
  `/opt/colossus/current` in place.
- [ ] Take a MySQL backup before an upgrade that changes persistence behavior.
- [ ] Stop the service, switch the `current` symlink, start it, and repeat the smoke checklist:

  ```bash
  sudo systemctl stop colossus
  sudo ln -sfn /opt/colossus/releases/NEW_RELEASE /opt/colossus/current
  sudo systemctl start colossus
  sudo journalctl -u colossus -n 100 --no-pager
  ```

- [ ] For an application rollback, point `current` at the previous release and restart. If the failed
  release made incompatible database changes, restore the paired MySQL backup before starting the old binary.
- [ ] Retain at least one known-good application release and one verified database backup until the new
  deployment has completed a real scheduled bake.
