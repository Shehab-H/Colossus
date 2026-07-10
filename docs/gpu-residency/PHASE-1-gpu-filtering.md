# Phase 1 — GPU Filtering

**Goal:** a filter change touches zero tile bytes. No fetch, no decode, no worker message, no
attribute upload — only deck layer props (uniforms). The tile cache stores exactly one entry per
`(version, tileKey)`.

**Non-goals:** color (Phase 2), decode copies (Phase 3), any UI change beyond one HUD label.

Read the README's hard constraints first. This phase is client-only — no bake changes.

---

## 0. Spike first (½ day, throwaway)

Before any real work, verify in a scratch branch that `DataFilterExtension` (from
`@deck.gl/extensions`, deck 9.3) works with **both** layer configurations used in
[App.tsx](../../web/src/App.tsx):

- `SolidPolygonLayer` with binary `data` (`attributes` + `startIndices`), `_normalize: false`, and
  an external `indices` attribute (bake-time triangles).
- `ScatterplotLayer` with binary `data`.

Hardcode a `getFilterValue` binary attribute and a `filterRange`, confirm marks disappear/reappear,
and confirm **picking** ignores filtered-out marks (click one — `onClick` must not fire for it).
If the extension does not compose with the external-indices polygon path, the fallback design is a
~30-line custom `LayerExtension` that injects the same test via the `DECKGL_FILTER_COLOR` vertex
hook and discards by setting alpha 0 + `discard` in the fragment hook — same attribute layout,
same props, so the rest of this phase is unchanged either way. Record which path was taken in the
commit message.

## 1. Design decisions (already made — do not relitigate)

### 1.1 Every filter is a numeric range on one float slot

deck's `DataFilterExtension` supports up to `filterSize: 4` float components with per-component
`filterRange`. All current filter semantics map onto numeric ranges, so **v1 uses only the numeric
path** (no `categorySize`):

| Channel role | Per-mark slot value | Filter → range |
|---|---|---|
| `temporal` | day number: days since Unix epoch, Float32 | `from..to` → `[dayOf(from), dayOf(to)]`; open side → sentinel (below) |
| `dimension` | **canonical category code** (see 1.2), Float32 | equality (UI is single-select) → `[code, code]` |
| inactive / `(all)` | — | `[-MAX_SAFE_F32, MAX_SAFE_F32]` |

Constants: use `const MAX_SAFE_F32 = 3.0e38` (finite, below f32 max — never `Infinity`, uniforms
are f32). Slot order = `filterableChannels(view)` order (deterministic, from the view config).

Rules the mapping must reproduce exactly (current behavior is the spec — see
[tileData.ts `rowsMatching`](../../web/src/lib/tileData.ts) and
[channels.ts](../../web/src/lib/channels.ts) before deleting them):

- A selected dimension value **not present in the canonical category list** matches nothing:
  encode range `[-2, -2]` (codes are ≥ 0, missing-value sentinel is a large positive — see 1.3).
- A temporal value with no real bounds (`parseDateRange` → null) is **not a predicate** → wide-open range.
- A filter on a channel the tile lacks matches nothing today; after 1.2 the slot value for a
  missing column is the missing sentinel (below), which no equality/range hits — same net behavior.
- **Null temporal values**: today `isoDate(null) → "null"`, and lexicographic comparison makes null
  rows pass any `from` bound and fail any finite `to` bound. Reproduce this exactly by encoding
  null/NaN temporal values as `NULL_DAY = 2.0e38` (passes `>= from`; fails `<= to` unless the `to`
  side is open, whose sentinel is `MAX_SAFE_F32 = 3.0e38 > NULL_DAY`). Add a unit test pinning this.

Day-number conversion must mirror `isoDate`'s storage heuristic (`tileData.ts`): a raw numeric `v`
is a day count when `|v| < 1e7`, else epoch-millis (`v / 86400000`); `Date` objects →
`ms / 86400000`; filter endpoint strings `YYYY-MM-DD` → `Date.UTC(y, m-1, d) / 86400000`. Day
numbers (≈ 20,000 today) are exactly representable in f32 — no precision concern. Truncate with
`Math.floor` consistently on both the column side and the endpoint side so "same day" compares equal.

### 1.2 Canonical category codes (shared foundation — Phase 2 reuses this)

Per-tile dictionary codes ([tileData.ts `dictFromArrow`/`stringColumn`](../../web/src/lib/tileData.ts))
are **tile-local**: two tiles may assign different codes to the same string. GPU filtering (and
Phase 2's LUT coloring) needs one code space per (view, version).

Canonical order, one function, one place — add to `channels.ts`:

```ts
/** The one canonical category order for a channel: baked full-extract domain when trustworthy,
 *  else the same sorted root-scan the color domain uses. Codes everywhere index THIS array. */
export function canonicalCategories(manifest: Manifest, channel: string): string[] | null
```

- `manifest.channelDomains[channel].values` when present and `!valuesTruncated` (this is the baked
  full-extract domain — no category can be missing).
- Else `null` → the caller must fall back (for filtering: fall back to the sorted option list from
  `discoverOptions`, which the UI already shows; a channel whose options are unknown can't be
  filtered by the UI anyway).
- `describeColorDomain`'s categorical branch must be refactored to use this same function so color
  categories and codes can never disagree (it already reads `channelDomains.values` — just route
  both through `canonicalCategories`).

At decode (in the worker), remap tile-local dict codes → canonical codes once per (tile, channel):
build `Map<string, number>` from the canonical array, then a `localCode → canonicalCode` LUT of
dictionary length (O(cardinality) string lookups, O(n) integer gather). A tile value absent from
the canonical list (should not happen with baked domains; can happen on legacy manifests) maps to
`MISSING_CODE = 2.0e38` — matched by nothing, included by `(all)`.

**Important:** the remap is for the *filter slot values and Phase 2 code attributes only*. The
`TileData.values` dict columns keep their local dictionaries — `columnValue` (inspect/click) and
the string form of values are untouched.

### 1.3 Filter slot values are built once, at decode, per tile

`TileData` gains one field:

```ts
filterValues?: Float32Array  // interleaved, length = count * slotCount (points)
                             //             or vertexCount * slotCount (polygons)
```

- Built in the worker inside `decodeTile`, after columns are read: for each mark, for each slot,
  the day number / canonical code / missing sentinel per 1.1–1.2.
- **Points** (`ScatterplotLayer` is instanced): per-mark, `count * slotCount`.
- **Polygons** (`SolidPolygonLayer` binary attributes are per-vertex): expand per-vertex using
  `polyStartIndices` — value of mark `p` repeated for vertices `[start[p], start[p+1])` — exactly
  the expansion pattern `tileDeckData` uses for colors today. `vertexCount * slotCount` floats.
  This is a one-time per-tile cost replacing today's per-filter-change full re-decode.
- `slotCount = min(filterableChannels(view).length, 4)`. Zero filterable channels → no
  `filterValues`, no extension on the layers.
- **> 4 filterable channels:** out of scope for v1. Add a console warning at manifest load and
  filter GPU-side on the first 4 slots only; channels beyond slot 4 get their HUD control disabled
  with a tooltip ("too many filter channels — not yet supported"). Do not silently render wrong
  data. (The designed escape hatch, if ever needed: fold overflow channels into one CPU-computed
  0/1 slot recomputed per filter change — an O(n) scan + one attribute update, still no fetch or
  geometry re-upload. Do not build it in this phase.)
- Memory math to be aware of (not a blocker): one f32 per slot per vertex. A 1M-mark point tile
  with 2 slots adds 8MB; count it in `tileBytes`.

### 1.4 The worker needs the canonical domains

The worker currently receives `{id, view, version, key, filters}`
([tileWorker.ts](../../web/src/lib/tileWorker.ts)). Replace `filters` with the data needed for slot
building:

```ts
{ id, view, version, key, slots: SlotSpec[] }
// SlotSpec = { name: string; kind: 'temporal' | 'dimension'; categories?: string[] }
```

Compute `SlotSpec[]` once per manifest on the main thread (a `useMemo` in `useTiles` or a helper in
the new `gpuFilter.ts`) from `filterableChannels(view)` + `canonicalCategories`. Categories arrays
are small (bounded by the bake's domain cap); structured-cloning them per load request is fine —
or, better, send them once per worker via an init message keyed by `version` and have load requests
reference that. Choose the simpler per-request clone unless profiling objects.

### 1.5 Layer props: filters ride on layers, not on tile identity

New module `web/src/lib/gpuFilter.ts` (pure, unit-tested — this is where the testable logic lives):

```ts
export interface FilterSlots { specs: SlotSpec[]; size: 1 | 2 | 3 | 4 }
export function filterSlots(manifest: Manifest): FilterSlots | null
export function buildFilterValues(slots: FilterSlots, tile: DecodedColumns): Float32Array
export function filterRanges(slots: FilterSlots, filters: Record<string, string>): [number, number][]
export function dayNumber(v: unknown): number          // mirrors isoDate's heuristic
export function dayNumberOfIso(s: string): number      // 'YYYY-MM-DD' endpoint → day
```

In `App.tsx`'s layers memo, for both layer types, when `slots` exist:

```ts
extensions: [dataFilterExtensionFor(slots.size)],   // module-level cache: Map<size, DataFilterExtension>
getFilterValue: undefined,                          // value comes from data.attributes (binary path)
filterEnabled: anyActive,                           // false when every slot is wide open
filterRange: ranges,                                // from filterRanges(slots, activeFilters)
```

and `tileDeckData` adds to `data.attributes`:

```ts
getFilterValue: { value: tile.filterValues, size: slots.size }
```

Critical invariant: `filterRange`/`filterEnabled` are **layer props**, so a filter change creates
new layer instances with the **same id and the same `data` object identity** — deck matches the
layer, diffs props, updates uniforms, and re-uploads nothing. Verify this explicitly during
acceptance (see §4). The `DataFilterExtension` instances must be module-level singletons per
`filterSize` — a fresh extension instance per render defeats prop diffing.

`countItems`/`filterRange` prop shapes: with `filterSize > 1`, `filterRange` is
`[[min,max], ...]` per slot; with `filterSize: 1` it is a flat `[min, max]`. Handle both in
`filterRanges`' return and a thin adapter at the layer.

## 2. Implementation steps (in order)

1. **`gpuFilter.ts` + tests.** Pure functions of §1.5. Unit tests
   (`web/src/lib/gpuFilter.test.ts`) must pin: slot ordering; equality → `[code,code]`; unknown
   value → `[-2,-2]`; `(all)`/empty-range → wide-open; open-ended date ranges; the null-temporal
   sentinel ordering (`NULL_DAY` vs open/closed `to` bounds); day-number heuristic parity with
   `isoDate` (feed the same raw values to both); per-vertex expansion against a hand-built
   `polyStartIndices`.
2. **`canonicalCategories` in `channels.ts`** + refactor `describeColorDomain`'s categorical branch
   onto it. Tests in `channels.test.ts`: baked domain preferred, truncated → null, parity with the
   color domain's category order.
3. **Decode changes in `tileData.ts`:**
   - `decodeTile(view, table, slots?)` — signature swaps `filters` for `slots`; builds
     `filterValues` (per-mark for points, per-vertex for polygons) after columns are read.
   - **Delete** `rowsMatching` and every `keep` parameter/branch from `readFields`,
     `numericColumn`, `stringColumn`, `dictFromArrow`, `utf8Column`, `materializeStrings`,
     `readPolygons`, `readTriangles`. Decode is now unconditional whole-tile. This is a large,
     satisfying deletion — do it completely; no dormant filter code remains.
   - `tileBytes` counts `filterValues`.
   - Update `tileData.test.ts`: delete filter-path tests, add `filterValues` construction tests
     (point + polygon, temporal + dimension slots, canonical remap, missing-category sentinel).
4. **Worker/loader plumbing:** `tileWorker.ts` message shape per §1.4; `tileLoader.ts`
   `load(view, version, key, slots)`; `transferable()` adds `filterValues`; the no-worker fallback
   path in `tileLoader.load` passes `slots` through to `loadTile`.
5. **Cache key collapse in `useTiles.ts`:**
   - `compositeKey(version, key)` — the `fkey` term and the `filters` hook parameter are removed;
     `filterKey` in `channels.ts` becomes dead → delete it and its tests.
   - `useTiles(manifest, camera, size)` — `App.tsx` stops passing `activeFilters` into it.
   - Everything else (cover logic, `abortStale`, `keepActive`, retry/back-off) is untouched.
6. **`App.tsx`:** compute `slots` (memo on manifest) and `ranges`/`anyActive` (memo on
   `[slots, activeFilters]`); attach extension + props to both layer types; pass `slots` to
   `useTiles`→loader (for the worker message) — thread it through the `ensure` closure where
   `tileLoader.load` is called.
7. **HUD honesty:** `marksLoaded` now counts *resident* marks (pre-filter), not rendered ones.
   Relabel the HUD counter accordingly ("marks resident" or similar — check
   `components/Hud.tsx` for the current label text). Do not compute a filtered count in this phase.
8. **Docs sync:** PLAN.md roadmap item 1 (GPU half done), RULES.md conformance note for R4,
   ARCHITECTURE.md frontend list (+`gpuFilter.ts`).

## 3. Behavior notes & edge cases (all deliberate)

- **Embed URLs** (`readEmbedParams().filters`) flow through `useViewData` state exactly as today —
  they end up as `activeFilters` → `filterRanges`. No embed change. Verify one embed URL manually.
- **Polygon view default slice:** `useViewData` defaults polygon views to one value per dimension.
  Previously non-matching rows were dropped at decode; now the whole tile is resident and the GPU
  discards. Resident memory worst case now equals today's `(all)` selection — bounded by
  `tilePointBudget` rows per tile, so the cache budget model is unchanged. This is accepted.
- **Picking:** `DataFilterExtension` excludes filtered marks from the picking pass — matches
  today's semantics (filtered rows didn't exist). Confirm by clicking a filtered-out region.
- **`atFullFidelity`** in `useTiles` keeps its meaning (leaves resident) — only the key changes.
- **Filter back-off/retry, cover tiles, zoom-swap behavior**: unchanged by design; if any of these
  need edits beyond the key collapse, something is wrong — stop and re-read.
- **`filterEnabled: false` when nothing is active** avoids shader cost on unfiltered views; the
  extension still being in `extensions` keeps layer prop shape stable (no program relink per
  filter-on/off — verify no hitch when toggling the first filter).

## 4. Acceptance criteria (all required)

1. `tsc`, `oxlint`, `vitest`, `dotnet test` green.
2. **Zero-work filter change:** with the GeoNames view loaded and network panel open — flipping any
   filter causes **0 `.arrow` requests, 0 worker messages** (assert via temporary counter or
   `preview_network`), and no `TileCache` mutation. Marks update on the next frame.
3. **No re-upload:** instrument once with deck's `device.statsManager` (luma stats, buffer memory)
   or simply assert `tileDeckData` cache hits: the `data` object identity per tile is unchanged
   across a filter flip.
4. **Pixel parity:** for each of (point view, polygon view) × (no filter, one dimension filter,
   one temporal range, combined): screenshot before-branch vs after-branch — identical rendered
   marks (the set of visible marks must match exactly; use a fixed camera).
5. **Baseline table updated** (README Phase 0): filter-change wall time now < one frame.
6. Old behavior preserved: view switching, zoom cover transitions, click-to-inspect on filtered and
   unfiltered marks, embed URL with filters, legacy manifest without `channelDomains` (filter via
   root-scan options still works — its option list is the canonical order in that case).

## 5. Risks / fallbacks

- **Extension × external-indices polygon path** — de-risked by the §0 spike; fallback extension
  design noted there.
- **f32 uniform precision** — day numbers and codes are small integers; no risk. Never put epoch
  *milliseconds* in a slot.
- **Legacy manifests (no baked domains):** `canonicalCategories` → null → slots fall back to the
  sorted `discoverOptions` list; correctness holds because both the codes and the UI options come
  from the same array. Cover with a unit test.
- **Attribute size limits:** `filterValues` for a 4-slot polygon tile is `vertexCount*16` bytes;
  fine for WebGL2. No action.
