# Phase 2 — GPU Color

**Goal:** recoloring (measure switch, scale change, theme flip) touches no per-mark data. The color
scale lives on the GPU as a small lookup-table texture + uniforms; the per-mark **value** column
uploads once per (tile, channel) and is reused across every scale/theme. `markColors` and the
per-vertex RGB expansion in [deckData.ts](../../web/src/lib/deckData.ts) are deleted.

**Prerequisite:** Phase 1 landed (canonical category codes exist; `DataFilterExtension` pattern for
attaching extensions + binary attributes is established).

**Non-goals:** decode copies (Phase 3), legend/HUD changes (legend already derives from the same
spec/domain on the CPU and stays as-is).

---

## 1. Design decisions (already made — do not relitigate)

### 1.1 The CPU scale stays the authority; the GPU gets a sampled LUT

[colorScale.ts `buildColorScale`](../../web/src/lib/colorScale.ts) remains the single
implementation of scale semantics (all types: linear/log/sqrt/diverging/quantize/quantile/
threshold/ordinal/categorical). The GPU consumes a **lookup table built by evaluating the existing
`ColorFn`** — parity by construction, testable without a GPU:

- **Numeric domains:** LUT of `N = 1024` RGBA8 texels. Texel `i` = `colorOf(invT(i / (N-1)))`
  where `invT` inverts the same normalization the shader applies (see 1.3). Banded scales
  (quantize/quantile/threshold/diverging with midpoint) are captured in value space with band-edge
  error ≤ domainSpan/1024 — accepted and documented tolerance.
- **Categorical domains:** LUT width = `categories.length` (canonical order from Phase 1's
  `canonicalCategories`), texel `c` = `colorOf(categories[c])`. One extra texel at the end holds
  the `unknown` color for out-of-domain codes.
- The `unknown`/null color (`ColorSpec.unknown`, and whatever `colorOf(undefined)` returns today)
  is also passed as a uniform for NaN handling.

New module `web/src/lib/colorLut.ts` (pure, unit-tested):

```ts
export interface ColorLut {
  texels: Uint8Array;            // RGBA8, width * 4
  width: number;
  domain: [number, number];      // numeric only
  transform: 0 | 1 | 2;          // 0 linear, 1 log, 2 sqrt — must mirror colorScale.ts normalization
  kind: 'numeric' | 'categorical';
  unknown: [number, number, number];
}
export function buildColorLut(spec: ColorSpec, domain: ColorDomain): ColorLut
```

Before implementing, read `colorScale.ts` and mirror its exact normalization per scale type
(including domain clamping and `reverse`) — the LUT bakes everything except the value→t transform,
which the shader must reproduce. If a scale type normalizes in a way the three transforms can't
express, bake that scale entirely into the LUT in linear value space (band error tolerance above)
rather than adding shader complexity.

### 1.2 One value attribute per (tile, channel); constant fill color

The deck `data` object per tile changes shape:

- `getFillColor` becomes the **constant** `[255, 255, 255, 255]` (a layer prop, not an attribute) —
  no per-vertex color array exists anywhere anymore.
- New binary attribute `getScaleValue` (float, size 1):
  - Numeric channel, points: the already-resident `Float32Array` column **by reference** — zero new
    allocation.
  - Numeric channel, polygons: per-vertex expansion (`vertexCount` floats), built lazily per
    (tile, channel) and cached exactly where `tileDeckData` memoizes today.
  - Dict channel: canonical codes as `Float32Array` (points per-mark, polygons per-vertex).
    Canonical remap reuses Phase 1's machinery. Out-of-domain → code `lut.width - 1` (the unknown
    texel) — encode as the code value at build time, not in the shader.
  - Raw-UTF8/string[] channel chosen as color (legal, degenerate): build codes by scanning through
    `canonicalCategories`; if null (truncated domain), fall back to… nothing fancy: map every value
    not found to unknown. This path is cold; keep it simple.
- `tileDeckData(d, channel, slots)` cache key becomes **`channel` only** (today it is
  `channel|scaleKey`). Scale changes no longer touch the data object at all. `scaleKey` remains in
  App.tsx solely to trigger LUT texture/uniform updates.

Memory note: this *reduces* steady-state memory vs today — one f32/vertex per touched channel
replaces 3 bytes × vertices × (channels × scale variants touched). Count expansions in `tileBytes`?
No — they live in the deckData WeakMap keyed by TileData and die with the tile; same lifetime rules
as today's color arrays. Leave `tileBytes` unchanged in this phase.

### 1.3 A small LayerExtension owns the shader work

New module `web/src/lib/colorScaleExtension.ts`, a `LayerExtension` (same shape as deck's own
extensions; the Phase 1 spike established the wiring pattern):

- **Attribute:** declared in `initializeState` via the layer's `AttributeManager`:
  `{ scaleValue: { size: 1, accessor: 'getScaleValue' } }` — instanced for `ScatterplotLayer`,
  vertex for `SolidPolygonLayer` (the attribute manager of each layer already implies this; supply
  data through `data.attributes.getScaleValue`, mirroring `getFilterValue`).
- **Uniforms** (use the deck 9 uniform-block style the installed version supports —
  check how `DataFilterExtension` declares its module in `@deck.gl/extensions` source and copy the
  pattern): `domain: vec2`, `transform: float`, `kind: float`, `unknownColor: vec3`,
  `lutWidth: float`, plus sampler `colorLut`.
- **Vertex shader injection** at the `DECKGL_FILTER_COLOR` hook:

  ```glsl
  float v = scaleValue;
  float t;
  if (kind == KIND_CATEGORICAL) {
    t = (min(v, lutWidth - 1.0) + 0.5) / lutWidth;      // NaN → unknown texel via max below
  } else {
    if (transform == T_LOG)  v = log(v);
    if (transform == T_SQRT) v = sqrt(v);
    t = clamp((v - domain.x) / (domain.y - domain.x), 0.0, 1.0);
  }
  vec3 rgb = (v != v) ? unknownColor : texture(colorLut, vec2(t, 0.5)).rgb;  // v!=v is the NaN test
  color.rgb = rgb;                                       // preserve color.a (layer opacity)
  ```

  For log/sqrt, `domain` is passed pre-transformed (`[log(min), log(max)]` etc.) so the shader does
  one transform, matching `invT` in 1.1. Texture lookup in the vertex shader is WebGL2/GLSL 300 es —
  supported everywhere deck 9 runs.
- **Texture lifecycle:** created in `initializeState`, re-written in `updateState` when the layer's
  `scaleLut` prop changes identity (App.tsx memoizes `buildColorLut` on `[colorSpec, domain]`, i.e.
  effectively on `scaleKey`). RGBA8, `NEAREST` min/mag, `CLAMP_TO_EDGE`, width×1. A few KB per
  layer is accepted (hundreds of layers ≈ low MBs); a shared per-device texture cache is noted as
  polish, not built now.
- Extension instance: module-level singleton (prop diffing, same rule as Phase 1).

### 1.4 What gets deleted / simplified

- `markColors` and the per-vertex RGB expansion in `deckData.ts` — deleted entirely.
- `tileDeckData`'s `colorOf`/`scaleKey` parameters — gone; it no longer knows about color at all
  beyond attaching `getScaleValue`.
- App.tsx layers memo: `colorOf` is no longer a dependency (it exists only to build the LUT);
  a recolor re-renders layers with same ids, same data, new `scaleLut`/uniform props.
- `useViewData`/legend/domain logic: unchanged. `buildColorScale` remains — used by the legend and
  by `buildColorLut`.

## 2. Implementation steps (in order)

1. **`colorLut.ts` + parity tests** (`colorLut.test.ts`): for every scale type exercised in
   `colorScale.test.ts`, assert LUT texels equal `colorOf` evaluated at texel centers, and for
   banded scales assert band edges land within one texel. Categorical: exact per-category match +
   unknown texel. Reuse the existing test fixtures/specs from `colorScale.test.ts` where possible.
2. **`colorScaleExtension.ts`**: shader module, attribute declaration, texture management. Keep it
   under ~150 lines; it should look like a sibling of deck's own extensions.
3. **`deckData.ts` rewrite**: delete color paths; add lazy `getScaleValue` per (tile, channel)
   (numeric by-reference for points; expansion/codes as per 1.2). Update `deckData.test.ts`:
   delete color tests, add value-attribute tests (reference identity for point numeric columns —
   assert `attributes.getScaleValue.value === tile.values[ch]`; expansion correctness for polygons;
   canonical code mapping; unknown handling).
4. **App.tsx**: constant `getFillColor`, extension + `scaleLut` prop on both layer types, memoized
   `buildColorLut`. Both extensions (filter + color) coexist in the `extensions` array — order:
   `[dataFilter, colorScale]`.
5. **Docs sync**: ARCHITECTURE.md frontend list (+`colorLut.ts`, `colorScaleExtension.ts`,
   deckData description update).

## 3. Acceptance criteria (all required)

1. `tsc`, `oxlint`, `vitest`, `dotnet test` green; LUT parity tests in place.
2. **Pixel parity** before/after branch at fixed camera for: numeric linear, log (a view whose
   measure spans decades if available, else a synthetic check via unit test only), categorical,
   threshold/quantize, plus `unknown` color on a channel with out-of-domain values. Tolerance: only
   band-edge pixels may differ (1.1); everything else byte-identical.
3. **Zero-data recolor:** switching color channel re-uses resident attributes when the channel was
   seen before (assert `tileDeckData` cache hit) and uploads only the new channel's value column on
   first touch; switching **scale/theme only** (`scaleKey` change, same channel) causes zero
   attribute uploads — only texture + uniforms. Verify via the same instrumentation used in
   Phase 1 acceptance §3.
4. Recolor wall time in the baseline table drops to ≤ one frame.
5. Click-to-inspect values, legend rendering, embed URLs with `color`/`colorSpec` overrides —
   unchanged.

## 4. Risks / fallbacks

- **Uniform/texture API drift in deck 9 minor versions:** copy the exact module/uniform declaration
  pattern from the installed `@deck.gl/extensions` source (`node_modules`), not from docs.
- **Banded-scale edge tolerance unacceptable in practice:** raise N to 4096 (16KB texture) before
  considering in-shader thresholds; only if a real view shows a visible artifact.
- **Per-layer texture memory:** only revisit (shared cache) if profiling shows texture churn; do
  not pre-build.
- **Points using the column by reference** couples GPU upload lifetime to `TileData.values` — fine
  today (both die with the tile); Phase 3's buffer-view change keeps this true (views die with the
  retained buffer). No action, just awareness.
