# Phase 4 — Fetch Locality (OPTIONAL)

**Status: build only when there is production latency data showing the network tier matters.**
After Phases 1–3, the only remaining fetches are *legitimate* (first visit to a tile, new LOD
level, evict-backtrack). This phase makes those cheaper; it does not change correctness or the
data model. Everything here is additive and independently landable.

Ordering within the phase: 4.1 → 4.2 are cheap and safe; 4.3 only if request-count pain is proven.

---

## 4.1 Persistent tile cache (Service Worker + Cache API)

**What:** a service worker that serves `GET <TILES_BASE>/<viewId>/<version>/…` cache-first from the
Cache API, so reloads and returning sessions render without touching the network even after the
browser's HTTP cache evicts.

Design decisions:

- Cache name per (view, version): `tiles-<viewId>-<version>`. On activation of a manifest (client
  posts a message to the SW after `loadManifest` resolves), the SW deletes `tiles-<viewId>-*`
  caches for other versions — version rotation is the GC.
- Only immutable tile paths are cached (`/<version>/` segment present). `latest.json`, manifests
  with `no-cache`, and the API are **never** intercepted — pass through.
- Quota safety: wrap `cache.put` in try/catch; on `QuotaExceededError`, delete the oldest tile
  cache entirely and retry once; on second failure, serve network-only. Never let quota errors
  surface as tile load failures.
- Dev mode: do not register the SW under `import.meta.env.DEV` (Vite HMR + SW is misery). Register
  in `main.tsx` for production builds only. The SW file lives in `web/public/sw.js` (plain JS, no
  bundler involvement) — keep it under ~80 lines; no workbox, no new dependencies.
- The client code path is untouched: `fetchArrowTable` still calls `fetch`; the SW is transparent.

Acceptance: build + preview the production bundle; load a view; hard-reload with the dev server's
tile route blocked (or offline) — tiles still render. Version flip (re-bake) invalidates cleanly.

## 4.2 Predictive prefetch

**What:** during pointer/scroll idle, warm the cache with the tiles most likely needed next.

Design decisions:

- Trigger: after the tile selection has been stable for ~300ms (debounce in `useTiles` on
  `selKeys` identity) and **all selected tiles are resident** — prefetch must never compete with
  demand loads.
- Candidate set, in priority order: (1) parents of the current selection (zoom-out is instant),
  (2) children of visible leaves' quads in the zoom direction of the last zoom gesture,
  (3) the one-tile ring around the viewport at the current level. Cap the whole set at 12 tiles
  per idle period.
- Budget guard: skip prefetch entirely when the cache is above 75% of `BUDGET_BYTES` — prefetched
  tiles must not evict anything (they enter via the normal `ensure`, so `keepActive` already
  protects the active set; the guard protects the *rest*).
- Cancellation: an in-flight prefetch is aborted by the existing `abortStale` when the selection
  changes (prefetch keys are simply not in the new active set — no new mechanism).
- Implementation: a small `prefetchTiles(manifest, selKeys, cache, loader)` helper called from the
  `useTiles` effect via `requestIdleCallback` (fallback `setTimeout(…, 200)` where unavailable).
  Pure candidate-selection function unit-tested in `tiling.test.ts` style.

Acceptance: zoom out after panning at a deep level — parents render without a visible fetch pause
(verify via network panel: parent fetches happened during idle, before the gesture).

## 4.3 Pack container (only with proven request-count pain)

**What:** replace per-tile files with one archive per (view, version) + a directory, fetched by
HTTP byte-range — PMTiles-shaped but private to Colossus (R7: on-prem, nginx serves ranges
natively; no new server code beyond emitting the archive at bake).

Sketch (do not build without owner sign-off):

- Bake writes `<version>/tiles.pack` (concatenated Arrow IPC tile messages, 64-byte aligned) and
  embeds `packDirectory: { "<z/x/y>": [offset, length] }` in `manifest.json` (or a sidecar
  `directory.json` if the manifest grows past ~1MB — decide by measuring a real manifest).
- Client `tileUrl` is replaced by a ranged `fetch(packUrl, { headers: { Range: bytes=… } })` when
  the manifest carries a directory; per-file layout remains supported (the directory's absence
  selects it). The SW (4.1) caches ranged responses keyed by tile key, not by Range header.
- Wins: fewer requests/connections at deep zoom, one immutable artifact per bake, trivially
  syncable. Costs: ranged-request caching is fiddlier, dev-server needs Range support (Kestrel
  static files support it; verify `Program.cs` config), and HTTP/2 already multiplexes — hence the
  "proven pain" gate.

## Explicitly rejected for this phase

- **zstd/content-encoding tuning:** tiles are already compact typed arrays; measure before adding
  decode CPU. Revisit only with real bandwidth data.
- **OPFS instead of Cache API:** more code (worker-side FS, own eviction) for the same outcome;
  Cache API wins unless SW registration is impossible in the deployment.
