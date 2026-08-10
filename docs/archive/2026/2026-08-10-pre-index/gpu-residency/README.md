# GPU Residency + Group/Measure Model — Completion Record

**Status: BUILT (v0), merged to main.** This initiative is done; the full plan, phase docs, and
build report were removed after completion and live in git history (last present at commit
`138aac8`). What remains here is the outcome, the invariant it established, and the one deferred
frontier file.

## What landed

1. **GPU filtering** — predicate filters are `DataFilterExtension` uniforms; the tile cache key
   lost the filter; decode-time filtering deleted. A filter change touches no tile bytes.
2. **GPU color** — color is a LUT texture + per-mark value attribute; the CPU recolor path
   deleted. A recolor is a ~4KB texture upload.
3. **Zero-copy tiles** — tile format 2 (`manifest.tileFormat`): single record batch, no nulls,
   tile-global triangles, canonical dictionary order, f32 measures; the client decodes as
   typed-array views over the one fetched buffer.
4. **Group/measure model (v0)** — group-regime views bake to a marks pyramid (one mark per
   geometry key) plus per-tile fact companions (`z/x/y.facts.arrow`, partial aggregates at grain,
   keyed by `mki`); the client folds companions under context filters and recolors via the
   Phase-2 seam. Semantics are normative in [VIEW_CONFIG.md](../VIEW_CONFIG.md) §1/§4.
5. **Fetch locality (4.1–4.2)** — service-worker tile cache + predictive prefetch. The 4.3 pack
   container was never built; its idea (one archive + byte-range directory) is subsumed by the
   packaging requirement in [companion-scale](../companion-scale/REQUIREMENTS.md), whose R2
   (leaf companions packed into one ranged archive, `manifest.companionPack`) is now built.

## The invariant this established (also recorded in RULES.md R4)

> **(version, tileKey) is the only data identity. Filter, measure, and color are GPU state —
> uniforms and small textures — never reasons to fetch, decode, copy, or re-upload tile data.**

## Still deferred

[PHASE-5-deferred-frontier.md](PHASE-5-deferred-frontier.md) — GPU buffer ownership, arena/
multi-draw, vertex pulling, WebGPU, SAB. Nothing there has a green light; each item carries its
own entry criteria. Its 5.3/5.4 GPU fold executor is the eventual consumer of the slab format
specified in [companion-scale](../companion-scale/REQUIREMENTS.md).
