# R2 build evidence — leaf packaging (built 2026-07-12)

[REQUIREMENTS.md](REQUIREMENTS.md) Requirement 2, built ahead of R1: the packer wraps whatever bytes
the leaf companion format produces (today the row form), so the R1 slab format later slots under it
unchanged.

## What was built

- **Bake** — `CompanionPackWriter` (Infrastructure/Tiles): after the pyramid, every leaf tile's
  `.facts.arrow` is gzip-compressed as an independent block into one `facts.pack` per (view, version),
  blocks in manifest tile order; the per-file leaf companions are deleted. The directory
  (`tileKey → [offset, length]`, codec) rides `manifest.companionPack`. Internal-level companions stay
  per-tile files. Row regime untouched (no `CompanionSpec` → no pack).
- **Client** — `packBlock()` resolves a tile against the manifest directory on the main thread;
  the tile worker range-reads the block (`fetchArrowBlock`: single `Range` request, exact-slice +
  browser-native `DecompressionStream`, then the usual Arrow decode). No directory entry → the
  per-file fetch (internal levels, older bakes). Companion/fold caching keys unchanged
  (`version|tile[|context]`). The service worker caches blocks per tile via the
  `facts.pack?tile=z/x/y` URL, re-wrapping 206→200 (the Cache API rejects partial responses).
- **Verifier** — the leaf companion witness (Σ leaf rows == source rows) reads through the pack.

## Before → after (mobile-dominance, the group-regime view; 7,607,947 facts / 627,511 marks)

| Metric | Before (v20260712T081835Z) | After (v20260712T130739Z) |
|---|---|---|
| Leaf companion storage | 48 files, 114,175,040 B | **1 archive, 39,588,082 B (2.88×)** |
| Largest single-tile companion fetch | 18,566,968 B (`3/2/5.facts.arrow`) | **6,413,304 B ranged block (2.90×)** |
| Version-dir file count | 139 | 92 |
| Version-dir total bytes | 374.7 MB | 303.5 MB |
| Internal companions (unchanged by design) | 21 files, 129.3 MB | 21 files, 129.3 MB |
| Manifest bytes (now carries the directory) | 18,775 | 21,864 |

Row-regime views (geonames, mobile-coverage, ookla-fixed) bake no companions and no pack; their new
bakes have identical tile sets and per-tile row counts. (Tile *bytes* differ between any two bakes —
two consecutive same-binary bakes of mobile-coverage differ too, the writer's row order within a tile
is not deterministic — so semantic identity is what verify witnesses.)

## Verification record (2026-07-12)

- `dotnet test` 117/117; `npx tsc -b`, `oxlint`, `vitest` 133/133 — green.
- Full rebake of all 4 registered views, then `bake -- verify`: **PASS ×4**, with mobile-dominance's
  companion witness decompressing all 48 blocks (Σ = 7,607,947 == source rows).
- Live range serve (Kestrel dev server): `Range: bytes=5152636-11565939` on `facts.pack` →
  **206**, correct `Content-Range`, body gunzips to exactly the pre-pack companion bytes
  (18,566,968 B, Arrow IPC marker). nginx serves ranges natively for prod (R7 note in REQUIREMENTS).
- Browser, app's own modules (`loadCompanion` + `packBlock`): leaf `3/2/5` → ranged block, 1,237,720
  rows decoded in ~109 ms; partial sum `sum__tests` = 6,977,762 — byte-equal to an independent
  Node (zlib + apache-arrow) decode of the same block. Internal `0/0/0` → `packBlock` null →
  per-file fallback decodes 1,225,878 rows. Group view renders with an active `operator` context,
  no console errors.
