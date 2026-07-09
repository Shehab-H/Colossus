# Colossus

Render very large datasets (10M–100M+ rows) with **no aggregation or simplification** of raw marks — on a map or in any chart — over a single engine:

```
source query (ClickHouse) → bake → Arrow IPC LOD tiles → static immutable serve → deck.gl binary attributes → GPU
```

A chart type is not a pipeline; it's `(mark + channel mapping) × reduction primitive`. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for how the code is organized, [docs/PLAN.md](docs/PLAN.md) for the full design, [docs/RULES.md](docs/RULES.md) for the hard invariants, and [docs/VIEW_CONFIG.md](docs/VIEW_CONFIG.md) for the config schema.

## Layout

| Path | What |
|------|------|
| `src/Colossus.Domain` | Models + ports: view config, manifest, tile math, reduction/source/bake interfaces |
| `src/Colossus.Application` | Use cases: bake planner + orchestration, fidelity verification |
| `src/Colossus.Infrastructure` | Adapters: ClickHouse source, DuckDB reducers, Arrow tile writer, file store, view registry |
| `src/Colossus.Bake` | Console entrypoint: bake registered views / `verify` |
| `src/Colossus.Server` | Dev host: static tiles + view registry API |
| `tests/Colossus.Tests` | Unit tests for the pure bake logic |
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

Run the tests with `dotnet test`; verify a bake's fidelity invariant with
`dotnet run --project src/Colossus.Bake -- verify`.

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

Batch engine works end-to-end: planner-chosen reduction, Arrow IPC LOD tiles, a config-driven renderer
with a full color-scale system (+ legend), click-to-inspect, a dark/light basemap toggle, and
URL-addressable embeddable maps. Interactive filtering and the queryable store are next — see the
roadmap in [docs/PLAN.md](docs/PLAN.md).
