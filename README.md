# Colossus

Render very large datasets (10M–100M+ rows) with **no aggregation or simplification** of raw marks — on a map or in any chart — over a single engine:

```
source (ClickHouse) → bake → Arrow LOD tiles → static immutable serve → deck.gl binary attributes → GPU
```

A chart type is not a pipeline; it's `(mark + channel mapping) × reduction primitive`. See [docs/PLAN.md](docs/PLAN.md) for the full design and milestone plan.

## Layout

| Path | What |
|------|------|
| `src/Colossus.Core` | Shared models + utils: `ViewDescriptor`, reduction strategies, tile math, Hilbert, Arrow helpers, manifest |
| `src/Colossus.Seed` | Synthetic dataset generator → ClickHouse |
| `src/Colossus.Bake` | Planner + extract + quadtree LOD reduction → Arrow tiles + manifest |
| `src/Colossus.Server` | ASP.NET Core dev host serving tiles (immutable cache) |
| `web/` | Vite + React + deck.gl + MapLibre; View-descriptor-driven renderer |
| `docker/` | Local ClickHouse (dev) |

## Dev quickstart

```bash
# 1. ClickHouse
docker compose -f docker/docker-compose.yml up -d

# 2. Seed synthetic data
dotnet run --project src/Colossus.Seed

# 3. Bake tiles
dotnet run --project src/Colossus.Bake

# 4. Serve tiles
dotnet run --project src/Colossus.Server

# 5. Frontend
cd web && npm run dev
```

## Status

Milestone 1 (batch walking skeleton) in progress — see the roadmap in `docs/PLAN.md`.
