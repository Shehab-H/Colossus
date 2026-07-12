# GPU Residency + Group/Measure Model — Charter & Index

**Status: BUILT (v0), merged to main.** Phases 1–3, the group/measure model (§1–9), and
fetch-locality 4.1–4.2 are complete — see [BUILD-REPORT.md](BUILD-REPORT.md) for evidence. Deferred:
fetch-locality 4.3 (pack container, owner gate) and [PHASE-5](PHASE-5-deferred-frontier.md) (entry
criteria unmet by design). The build executed as a single combined workload in this order:

1. **[PHASE-1-gpu-filtering.md](PHASE-1-gpu-filtering.md)** — filters become GPU uniforms; cache
   key loses the filter; decode-time filtering deleted.
2. **[PHASE-2-gpu-color.md](PHASE-2-gpu-color.md)** — color becomes LUT texture + value attribute;
   CPU recolor deleted. (This seam is where group/measure folded values land later.)
3. **[PHASE-3-zero-copy-tiles.md](PHASE-3-zero-copy-tiles.md)** — tile format v2 + view-based
   zero-copy decode.
4. **[GROUP-MEASURES.md](GROUP-MEASURES.md)** — the group/measure model end to end (marks bake,
   fact companions, client fold, computed color), whose semantics are normative in
   [VIEW_CONFIG.md](../VIEW_CONFIG.md) §1/§4. Its predicate filters ARE Phase 1; its recolor path
   IS Phase 2's seam.

[PHASE-4-fetch-locality.md](PHASE-4-fetch-locality.md) stays optional (production data gate) and
[PHASE-5-deferred-frontier.md](PHASE-5-deferred-frontier.md) stays deferred (the GPU fold executor
in 5.3/5.4 replaces the CPU fold behind the same interface when scale demands it).

Each step lands as its own commit series, tests green, before the next begins. The implementer
maintains **BUILD-REPORT.md** in this folder — per-phase status, evidence, and deviations — as the
single source of build truth.

This folder is a complete, self-contained implementation plan. It is written for an implementing
agent that has NOT seen the design discussion that produced it — everything needed is in these files
plus the repo. Read this README fully before opening any phase file.

---

## The problem being solved

The client re-runs the entire tile pipeline for interactions that change **no data**:

1. **Filter change** is the worst case. The tile cache key is `version|filterKey|tileKey`
   ([useTiles.ts:18](../../web/src/hooks/useTiles.ts)) and filters are applied *inside decode* in the
   worker ([tileData.ts `decodeTile`/`rowsMatching`](../../web/src/lib/tileData.ts)). So flipping one
   filter re-runs, for every visible tile: `fetch()` (usually HTTP-cache-served, but still an
   `arrayBuffer()` copy), Arrow parse, an O(n) predicate scan, gather-copies of every column and
   geometry buffer, a triangle-rebase loop, a worker transfer, a new `TileData` identity, and — the
   expensive part — a **full GPU re-upload of geometry that did not change**. The cache also holds a
   duplicate copy of each tile per filter state, all competing for one 384MB budget.
2. **Recolor** (measure switch, scale change, theme) runs the color scale per mark on the CPU and
   expands to a per-vertex RGB array (`vertexCount*3` bytes) per (channel, scaleKey) in
   [deckData.ts `markColors`](../../web/src/lib/deckData.ts) — the largest recurring heap allocation
   in the app — then uploads it.
3. **Decode copies everything.** [tileData.ts `readFields`](../../web/src/lib/tileData.ts) copies
   every column out of the Arrow message, `readPolygons` slices positions, `readTriangles` rebuilds
   the whole index buffer to rebase row-local indices. The "zero-copy" claim in
   [arrow.ts](../../web/src/lib/arrow.ts) is aspirational, not current.

## The principle every phase serves

> **(version, tileKey) is the only data identity. Filter, measure, and color are GPU state —
> uniforms and small textures — never reasons to fetch, decode, copy, or re-upload tile data.**

Once this holds: a filter change is a uniform update (microseconds, same frame), a recolor is a
~4KB texture upload, the cache never stores a tile twice, and a tile's bytes flow
network → worker → GPU exactly once.

## Phases

| Phase | File | What | Depends on | Gate |
|---|---|---|---|---|
| 0 | this file, § Baseline | Measure before touching anything | — | mandatory first step |
| 1 | [PHASE-1-gpu-filtering.md](PHASE-1-gpu-filtering.md) | Filters become GPU uniforms (`DataFilterExtension`); cache key loses the filter; decode-time filtering deleted | Phase 0 | land first — highest leverage |
| 2 | [PHASE-2-gpu-color.md](PHASE-2-gpu-color.md) | Color becomes a LUT texture + value attribute; `markColors` deleted | Phase 1 (shares canonical category codes) | land second |
| 3 | [PHASE-3-zero-copy-tiles.md](PHASE-3-zero-copy-tiles.md) | Tile format v2 (bake) + view-based zero-copy decode (client) | Phases 1–2 (decode is simplest after filter/color moved) | land third |
| 4 | [PHASE-4-fetch-locality.md](PHASE-4-fetch-locality.md) | Service-worker tile cache, prefetch, optional pack container | Phase 3 | OPTIONAL — only with production latency data |
| 5 | [PHASE-5-deferred-frontier.md](PHASE-5-deferred-frontier.md) | GPU buffer ownership, arena/multi-draw, vertex pulling, WebGPU | — | DO NOT BUILD — entry criteria inside |

Land each phase as its own commit series with tests green and the baseline re-measured. Never
combine phases in one change. Phases 1–3 are pure engine work with zero intended visual change —
every phase's acceptance includes pixel-identical rendering (within documented tolerances).

## Hard constraints for the implementing agent

1. **[RULES.md](../RULES.md) is the authority.** Nothing here overrides R1–R7. Where this plan
   touches the tile schema (Phase 3), it stays within R3; if you believe a change requires amending
   RULES.md, stop and ask the owner — do not work around it silently.
2. **Layer/data identity never includes measure, filter, or scale.** This invariant already exists
   ([App.tsx layers memo](../../web/src/App.tsx), [useTiles.ts](../../web/src/hooks/useTiles.ts))
   and every phase deepens it. Deck must always be able to match an existing layer by id and see the
   same `data` object identity across filter/recolor interactions — that is what prevents GPU
   re-uploads. Breaking it silently re-introduces the exact problem this initiative removes.
3. **Data-agnostic always.** No dataset-specific logic. GeoNames is the stress *test*, never a
   special case in code.
4. **Comment style: minimal.** Match the existing code — comments state constraints and invariants
   the code can't show, never narrate mechanics. No verbose blocks.
5. **Backward compatibility:** the client must keep rendering format-1 tiles (current bakes) until
   the owner re-bakes every view. Format gates are explicit (`manifest.tileFormat`, Phase 3).
6. **Verification per phase** (all must pass before a phase is "done"):
   - `cd web && npx tsc -b && npm run lint && npm run test`
   - `dotnet test` (from repo root)
   - `dotnet run --project src/Colossus.Bake -- verify` (after any bake-side change, against a fresh bake)
   - The manual browser scenarios listed in each phase's Acceptance section, driven through the
     preview harness (`.claude/launch.json` has the dev servers).
7. **No new dependencies** without explicit need stated in the phase file. Phase 1–3 require none
   (`@deck.gl/extensions` ships with the already-installed deck.gl 9.3.6 — add it to
   `web/package.json` dependencies explicitly since it is a separate npm package in deck 9).

## Phase 0 — Baseline (mandatory, do this first)

Create `docs/gpu-residency/BASELINE.md` and fill this table **before Phase 1**, then append a
re-measured column after each landed phase. Use the GeoNames stress view (seeded by the repro
script — see recent commit `131daf9`) and one polygon view. Dev servers: start via the preview
harness using `.claude/launch.json`.

Measurements (describe method next to each number):

| Metric | How to measure |
|---|---|
| Filter change → settled, wall ms | In DevTools/`preview_eval`: `performance.mark` before `setFilters` dispatch (drive the HUD select), `requestAnimationFrame` after the last tile lands (watch network idle + no cache misses); report p50 of 5 runs |
| Tile fetches per filter change | `performance.getEntriesByType('resource')` delta filtered to `.arrow`, or `preview_network` |
| Worker decode per filter change | `console.time` instrumentation added temporarily in `tileWorker.ts` (remove after) |
| Recolor (scale/measure switch) wall ms | same marking technique around the HUD color-channel select |
| JS heap delta across 5 filter flips | `performance.memory.usedJSHeapSize` before/after, after a forced idle |
| Long tasks (>50ms) during a zoom swap | `PerformanceObserver({entryTypes:['longtask']})` |
| Resident cache bytes | temporary log of `TileCache` total `sizes` |

Expected orders of magnitude (sanity check, not targets): filter change today 150–500ms with
10–20 visible tiles; after Phase 1 it must be < 16ms (one frame) with **zero** `.arrow` fetches and
**zero** worker messages.

## File map (what the plan refers to)

Client (all under `web/src/`):

- `hooks/useTiles.ts` — viewport → tile selection → cache orchestration; owns the composite key.
- `hooks/useViewData.ts` — manifest load, filter state/defaults, color channel/domain/scale.
- `lib/tileCache.ts` — bounded store, `version|filter|tile` keys (Phase 1 removes the filter part).
- `lib/tileLoader.ts` / `lib/tileWorker.ts` — worker pool; fetch + decode off the main thread.
- `lib/tileData.ts` — Arrow → `TileData`; decode-time filtering lives here today (Phase 1 deletes it).
- `lib/deckData.ts` — memoized deck binary attributes; CPU coloring lives here today (Phase 2 deletes it).
- `lib/channels.ts` — filter parsing (`parseDateRange`, `ALL`, `RANGE_SEP`), `filterKey`,
  `describeColorDomain`, `discoverOptions`, channel role helpers.
- `lib/colorScale.ts` — `buildColorScale(spec, domain) → ColorFn`; stays the CPU authority that the
  GPU LUT is built from and tested against.
- `lib/manifest.ts` — manifest types (`ChannelDomain`, `tileUrl`); gains `tileFormat` in Phase 3.
- `lib/schema.ts` — client mirror of the canonical tile schema.
- `App.tsx` — layer construction; where filter/color GPU props attach.

Bake (all under `src/`):

- `Colossus.Domain/Tiling/TileSchema.cs` — canonical schema authority (R3).
- `Colossus.Infrastructure/Tiles/ArrowTileWriter.cs` — writes one RecordBatch per tile (already
  single-chunk); row-local `triangles` today (Phase 3 makes them tile-global under format v2).
- `Colossus.Infrastructure/Tiles/ArrowColumnBuilder.cs` — per-column builders; per-tile dictionary
  encoding today (Phase 3 makes dictionary order canonical).
- `Colossus.Infrastructure/Tiles/PolygonTriangulator.cs` — bake-time tessellation (row-local indices).
- `Colossus.Domain/Model/Manifest.cs` — manifest model; gains `TileFormat` in Phase 3.
- `Colossus.Application/BakeViewUseCase.cs` — bake orchestration; where channel domains are computed.

Docs to keep in sync when phases land:

- [VIEW_CONFIG.md](../VIEW_CONFIG.md) support markers — Phase 1 turns auto-derived filters fully
  live GPU-side; the group/measure phases flip §4's status when they land.
- [RULES.md](../RULES.md) "Current conformance" R4 note ("Interactive `filters` … not yet honored") —
  update when Phase 1 lands.
- [ARCHITECTURE.md](../ARCHITECTURE.md) frontend file list — new modules from Phases 1–2.

## Known numbers (context)

- deck.gl **9.3.6**, apache-arrow **21.1.0**, React 19, Vite 8 (see `web/package.json`).
- Tile serve: immutable, `max-age=31536000` (`src/Colossus.Server/Program.cs`) — so today's
  filter-change "refetch" is HTTP-cache-served; the waste is decode + copies + GPU re-upload, not
  usually the network.
- Cache budget 384MB (`web/src/lib/tileCache.ts`); a GeoNames point tile ≈ 12MB decoded, ~1M marks.
- `DICT_CAP` 65536 (`tileData.ts`): categorical columns above this fall back to raw UTF-8.
