# Render pack — format and the block-order rule

The render pack (tile-transfer Phase 3) replaces the per-tile render files of a baked version with one
archive. Authority: [`RenderPack`](../../src/Colossus.Domain/Model/Manifest.cs) (contract) and
[`RenderPackWriter`](../../src/Colossus.Infrastructure/Tiles/RenderPackWriter.cs) (writer + reader).

> **Status.** The bake writes the pack and `verify` reads through it. **The client does not read it yet** —
> the browser still takes the per-tile path. Until that lands, do not re-bake a view you need to render:
> a packed version has no per-tile files for the client to fall back to. See "Not yet landed" below.

## Why

Phase 2 (tile format 3) removed the derivable geometry. What is left in a tile is mostly **real-valued f32
measure planes**, which are incompressible by construction — ~1.1–1.3x and no more. There is no lossless
transform that shrinks them.

The remaining win is therefore not compression but **omission**: a first paint reads geometry and exactly
one colour channel, so every other measure plane is bytes the viewer paid for and never looked at. The pack
makes each column independently addressable so those bytes are simply not sent until an interaction reads
them.

## Layout

One archive per (view, version), `render.pack`, beside the manifest. Blocks are concatenated with no
padding or framing between them; the manifest carries the directory.

A **group** is one column, with one exception: the geometry group `@geom` holds whatever makes the mark
drawable in a single block — the encoded `geom3` payload for area marks, the `x`/`y` pair for point marks.
The group is chosen from the tile's own Arrow schema, never from view config, so a new mark type needs no
format change.

Each block is a **standalone single-batch Arrow IPC stream** carrying just that group's columns (plus the
tile's schema metadata, so a block stays self-describing). This is what preserves the zero-copy contract:
a column decodes alone and remains a typed-array view over its own inflated buffer. It costs Arrow framing
per block, which is the deliberate trade — see "Costs".

Compression is **per block, inside the archive**, because `Content-Encoding` does not compose with Range
requests. Codec is zstd level 19 with a trained shared dictionary (`render.dict`, one per view/version),
mirroring the slab companion path: small per-column blocks compress poorly alone, and the dictionary is
what makes them worth slicing. Too few blocks to train on ⇒ plain zstd, same codec name.

```
manifest.renderPack = {
  file: "render.pack",
  codec: "zstd",
  entries: { "<z>/<x>/<y>": { "<group>": [offset, length], … }, … },
  firstPaint: ["@geom", "<colour channel>", "<filter slot>", …],
  dict: "render.dict", dictHash: "<sha256 hex>"
}
```

Absence of `manifest.renderPack` selects the per-tile file path — formats 1/2 and every older bake. This is
the same gating pattern `companionPack` uses, and it is why old versions keep rendering untouched.

## The block-order rule

**Within a tile's span, blocks are laid down in this order and no other:**

1. `@geom`
2. the default colour channel
3. the filter-slot channels
4. everything else

This is a load-bearing invariant, not a tidiness preference. It buys two properties at once:

- **The default first paint is ONE contiguous byte range per tile.** Groups 1–3 are exactly
  `manifest.renderPack.firstPaint`, and they sit adjacent at the head of the span, so the client coalesces
  them into a single Range request rather than N small ones.
- **A whole-tile read is still ONE range** over the tile's full span, because the lazy groups follow
  immediately and tile spans never interleave.

A colour switch away from the default costs exactly two runs (the contiguous `@geom + slots` head, plus the
switched channel wherever it sits) — that is designed behaviour, not degradation.

The order is derived from the view's **declared roles** — `encoding.color.channel`, then `filters[].channel`
— by `RenderPack.FirstPaintChannels`. No channel name and no dataset shape appears in the writer. A view
declaring neither role simply has a geometry-only first paint.

**This constrains future formats the way the slab format's cell order does.** Any new column kind must
declare which of the four positions it occupies. Appending a column blindly to the end is safe; inserting
one between groups 1 and 3 without adding it to `firstPaint` silently splits every tile's first-paint run
into two Range requests.

## At rest

A packed bake keeps **no** per-tile render file: no `z/x/y.arrow`, and no `z/x/y.arrow.br` sibling. This is
where the recorded "drop uncompressed at rest" follow-up lands for real. Phase 1's brotli pass is skipped
entirely for packed bakes rather than run-then-deleted, and `PrecompressedTiles` middleware stays in place
for already-published per-file versions.

## Costs

Per-block Arrow framing is real: ~300–600 uncompressed bytes per block, times one block per column per tile.
The trained dictionary absorbs most of it, but a tile with many columns and few rows pays proportionally
more. The accepted budget is **≤5% regression in whole-tile bytes versus Phase 2**; the alternative (raw
typed-array blocks, as the slab planes use) would avoid the framing but would require re-encoding dictionary
and UTF-8 columns by hand, i.e. a second column format to keep losslessly in sync. Arrow-per-block was
chosen because it reuses the existing decode path verbatim.

## Reading a block

`RenderPackWriter.ReadBlock` bounds the read to the block's length before inflating — blocks are
concatenated, so decoding must stop at the boundary rather than run into the next block. `RowCount` reads
through `@geom` alone: every block in a tile's span has the same row count, so fidelity is witnessed without
inflating a single measure plane.

## Not yet landed

- The client read path (worker-pool block fetch, lazy column merge, colour-switch and inspect-click fetches).
- Point-view (`geonames`) validation — the writer's x/y `@geom` grouping is implemented but unexercised.
- Re-bakes of the six views and the measured first-paint / whole-tile / interaction numbers.

The service worker needs no change: `PACK_RE` in `web/public/sw.js` already matches `render.pack`, and its
cache is keyed `tiles-{viewId}-{version}` with the byte range as a sub-key — the identity the pack requires.
