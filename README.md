# Colossus

Render very large datasets (10M–100M+ rows) with **no aggregation or simplification** of raw marks — on a map or in any chart — over a single engine:

```
source query (ClickHouse) → bake → Parquet LOD tiles → static immutable serve → DuckDB-WASM + deck.gl → GPU
```

A chart type is not a pipeline; it's `(mark + channel mapping) × reduction primitive`. See [docs/PLAN.md](docs/PLAN.md) for the full design and milestone plan.

## Layout

| Path | What |
|------|------|
| `src/Colossus.Core` | Domain models + ports: view config, reduction strategies, tile math, manifest |
| `src/Colossus.Bake` | Planner + extract + reduction → tiles + manifest |
| `src/Colossus.Server` | Dev host: static tiles + view registry API |
| `web/` | Vite + React + deck.gl + MapLibre; config-driven renderer |
| `views/` | View config files (the registry) |
| `docker/` | Local ClickHouse (dev) |

## Dev quickstart

Colossus reads from your own ClickHouse tables — there is no bundled data generator. Point a view at your data ([docs/VIEW_CONFIG.md](docs/VIEW_CONFIG.md)), then:

```bash
# 1. ClickHouse
docker compose -f docker/docker-compose.yml up -d

# 2. Bake a view's tiles
dotnet run --project src/Colossus.Bake -- <view-id>

# 3. Serve tiles + view API (Swagger UI at /swagger)
dotnet run --project src/Colossus.Server

# 4. Frontend
cd web && npm run dev
```

## Status

Milestone 1 (batch walking skeleton) in progress — see the roadmap in `docs/PLAN.md`.
