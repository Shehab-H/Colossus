# Deploying Colossus — SmarterASP.NET + object storage (R2)

Going-live topology (chosen 2026-07-12):

```
Browser
  ├─ HTML/JS/CSS  ──►  SmarterASP.NET  (ASP.NET Core, IIS in-process)
  │                      /                → React SPA        (wwwroot)
  │                      /showcase/       → showcase page    (wwwroot/showcase)
  │                      /api/views       → dataset picker   (views/*.json)
  └─ tiles + manifests ──►  Cloudflare R2 (+ CDN)  → /{view}/{version}/z/x/y.arrow, facts.pack, manifest.json
```

Why this split: the tiles are ~6 GB of **static immutable** files — wrong thing to put on an app
server, right thing for object storage with a CDN (R2 = zero egress, 10 GB free tier). Every view's
full config (measures + encoding) is embedded in its `manifest.json`, so **the map renders entirely
from R2**; the SmarterASP.NET app only serves the frontend shell and the (optional) dataset picker.

**Independent origins.** The SPA, the view API, and the tiles may each live on their own subdomain —
nothing in the app assumes they share an origin. The client is pointed at each via `VITE_TILES_BASE`
and `VITE_API_BASE` (absolute URLs; Phase 2). The server sends permissive CORS (`AllowAnyOrigin` +
exposed `Content-Range`/`Accept-Ranges`/`Content-Length`, see `Program.cs`), so the browser may call the
API from the SPA's origin. The tiles host sets its own CORS (Phase 1 step 4) — it must allow the SPA's
origin and the `Range` header, since `facts.pack` reads are ranged and cross-origin.

**Remote fold (companion-scale R4) — only for views the planner prices `remote`.** The bake prices every
group-regime view's per-interaction companion transfer and records the route in its manifest
(`foldRoute.execution`: `client` | `remote`). A `client` view needs nothing here — it folds in the browser
over companion planes read from R2, exactly as before. A `remote` view instead POSTs its context to
`/api/views/{id}/fold` on the app server, which folds in DuckDB over that version's **baked**
`facts.parquet` and returns folded columns. Two consequences for this topology:

- **The tiles stay static.** The fold endpoint is additive; `z/x/y.arrow` and `facts.pack` are still
  immutable files on R2 (RULES R7). Nothing about the tile serve changes.
- **A `remote` view's `facts.parquet` must sit where the SERVER can read it** — i.e. in the app server's
  `TilesRoot` at `<viewId>/<version>/facts.parquet`, not only on R2 (DuckDB reads it off the local disk).
  Ship it alongside the app in Phase 3 for those views only. It is the fold's input, never fetched by the
  browser. All four views today price `client`, so nothing needs shipping; check the bake log line
  (`fold route = …`) or the manifest before assuming.

The budget is a **bake-time** setting — `FoldRouting:BudgetBytes` in `src/Colossus.Bake/appsettings.json`
(**default 32 MB**: a view whose worst leaf tile moves more than that per interaction routes remote; the
reference views measure ~8 MB after R5's plane split and stay client). Routing is decided once, at bake, and
baked into the manifest, so serving needs no such config.
`COLOSSUS_FOLD_FORCE_REMOTE=1` forces every group view remote at bake, and `?fold=remote` forces the client
onto the remote route for one session — both for testing/benchmarking only.

Two facts about this stack that drive the steps below:

1. **Target is `net10.0`** — brand new; SmarterASP.NET's shared servers almost certainly don't have
   that runtime. We therefore publish **self-contained** (the runtime is bundled), so the host needs
   nothing pre-installed.
2. **Tiles are ~6 GB** (latest versions only; ~11 GB counting stale versions) and are **not in git** —
   they exist only on the machine that baked them, so "deploy tiles" = upload them to R2.

---

## Phase 0 — Trim tiles to latest-only (local)

Each view keeps several old version folders; only the one named in `latest.json` is needed.

```powershell
foreach ($v in 'geonames','mobile-coverage','mobile-dominance','ookla-fixed') {
  $keep = (Get-Content "tiles/$v/latest.json" | ConvertFrom-Json).version
  Get-ChildItem "tiles/$v" -Directory | Where-Object Name -ne $keep | Remove-Item -Recurse -Force
  Write-Host "$v -> kept $keep"
}
# tiles/ should now be ~6 GB
```

---

## Phase 1 — Publish tiles to Cloudflare R2

Needs a (free) Cloudflare account. Backblaze B2 works the same way if you prefer.

1. **Create a bucket** (e.g. `colossus-tiles`) and an **R2 API token** (Access Key ID + Secret).
2. **Upload the tile tree to the bucket root**, preserving structure so `{base}/{view}/latest.json`
   and `{base}/{view}/{version}/z/x/y.arrow` resolve. Using [rclone](https://rclone.org):

   ```powershell
   rclone config create r2 s3 provider Cloudflare `
     access_key_id <KEY> secret_access_key <SECRET> `
     endpoint https://<ACCOUNT_ID>.r2.cloudflarestorage.com

   # immutable data EXCEPT render tiles: long cache. .arrow is uploaded separately (br, below); .arrow.br
   # is never its own key (the client only ever requests .arrow). facts.pack / facts.dict / facts.parquet /
   # manifest.json go up plain here — the pack is range-read, so it must NOT carry Content-Encoding.
   rclone copy tiles r2:colossus-tiles --transfers 16 --checkers 16 `
     --header-upload "Cache-Control: public, max-age=31536000, immutable" `
     --exclude "*/latest.json" --exclude "*.arrow" --exclude "*.arrow.br"

   # pointer files: short cache (they change on every rebake)
   rclone copy tiles r2:colossus-tiles --include "*/latest.json" `
     --header-upload "Cache-Control: public, max-age=60"
   ```

   Then publish the **render tiles as always-br** — see "Transport compression" below.

3. **Make it publicly reachable** (R2 → bucket → Settings):
   - Quick start: enable the **r2.dev** public subdomain → `https://pub-xxxx.r2.dev`. (Rate-limited;
     fine for a demo, not recommended for sustained production traffic.)
   - Production: connect a **custom domain** — full Cloudflare CDN + caching.
4. **Set the bucket CORS policy** (R2 → bucket → Settings → CORS) so the browser may fetch tiles
   cross-origin, including ranged `facts.pack` reads:

   ```json
   [
     {
       "AllowedOrigins": ["https://YOURSITE.smarterasp.net"],
       "AllowedMethods": ["GET", "HEAD"],
       "AllowedHeaders": ["Range", "Content-Type"],
       "ExposeHeaders": ["Content-Range", "Content-Length", "Accept-Ranges", "ETag"],
       "MaxAgeSeconds": 86400
     }
   ]
   ```

Note the public base URL — call it `R2_BASE` (e.g. `https://pub-xxxx.r2.dev`). The client builds
`R2_BASE/{view}/latest.json`, so if you nested the tree under a `tiles/` prefix instead of the root,
append `/tiles` to `R2_BASE`.

### Transport compression (brotli render tiles)

Render tiles are Arrow IPC (`application/vnd.apache.arrow.stream`), which is **not** on Cloudflare's
compressible-MIME list — served as-is they never compress, so a viewport moves several MB uncompressed.
The bake fixes this at rest: every `z/x/y.arrow` is written with a **brotli** sibling `z/x/y.arrow.br`
(quality 11 — ~5.1–5.5x on the large views, beating whole-file zstd-19). Nothing else changes — the plain
`.arrow` is still written (the rollback rail), and the client is untouched: a browser decodes
`Content-Encoding` in the network stack, so `fetch → arrayBuffer()` yields the identical `.arrow` bytes and
the zero-copy decode path never sees compression.

Backfill any already-baked version (no re-bake — tiles are immutable per version, so adding a sibling is
identity-safe) before uploading:

```powershell
dotnet run --project src/Colossus.Bake -- compress          # all views; add view ids to scope it
```

Object storage can't content-negotiate, so the client's `.arrow` request must resolve to a single, br
representation: **upload the `.br` bytes under the plain `.arrow` key, tagged `Content-Encoding: br`.** Stage
the br files under their plain names (hardlinks — no byte copy), then one `rclone` copy carries the header:

```powershell
$stage = "tiles-br"
Get-ChildItem tiles -Recurse -Filter *.arrow.br | ForEach-Object {
  $rel  = $_.FullName.Substring((Resolve-Path tiles).Path.Length + 1)   # view/ver/z/x/y.arrow.br
  $dest = Join-Path $stage ($rel -replace '\.br$', '')                  # view/ver/z/x/y.arrow
  New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
  New-Item -ItemType HardLink -Path $dest -Target $_.FullName | Out-Null  # Copy-Item if $stage is another volume
}
# The .arrow keys: br bytes + the header that tells browsers to decode them.
rclone copy $stage r2:colossus-tiles --transfers 16 --checkers 16 `
  --header-upload "Content-Encoding: br" `
  --header-upload "Cache-Control: public, max-age=31536000, immutable"
Remove-Item -Recurse -Force $stage
```

These objects are **always-br** — there is no uncompressed representation at the `.arrow` key. That is exactly
what a browser wants (it always sends `Accept-Encoding: br` and decodes transparently), but it **breaks a bare
`curl`**, which will dump raw brotli: use `curl --compressed <url>/….arrow` to inspect one by hand. `facts.pack`
is deliberately left plain (excluded above): it is read by HTTP **Range**, and `Content-Encoding` does not
compose with ranged requests.

**Rollback** is a plain re-upload over the same keys — the local `.arrow` files always exist because the bake
never stops writing them:

```powershell
rclone copy tiles r2:colossus-tiles --transfers 16 --checkers 16 --include "*.arrow" `
  --header-upload "Cache-Control: public, max-age=31536000, immutable"
```

The plain body differs in size from the br object, so rclone re-uploads, and a fresh PUT **replaces the
object's metadata** — the `Content-Encoding: br` tag is gone and the tiles serve uncompressed again. No client
change either way.

> The dev server (`src/Colossus.Server`) mirrors this: it answers a `*.arrow` request with the `.arrow.br`
> sibling + `Content-Encoding: br` when the client accepts br and the sibling exists, else the plain file. So
> `npm run dev` against a local bake exercises the same compressed path the browser sees in production.

---

## Phase 2 — Build the frontend (point it at each origin)

```powershell
cd web
$env:VITE_TILES_BASE = "https://pub-xxxx.r2.dev"   # your R2_BASE (no trailing slash)
$env:VITE_API_BASE   = "/api"                       # same-origin app; or an absolute URL if the API
                                                    #   is its own subdomain, e.g. https://api.yoursite.com
npm run build                                       # -> web/dist
cd ..
```

`VITE_API_BASE` is only guessed from `VITE_TILES_BASE` when unset (it assumes the API sits beside the
tiles) — always set it explicitly when the three roles are on separate origins. The server's CORS
(above) already permits the cross-origin call; no API code change is needed.

Point the showcase page at the same site it will be served from (edit `showcase/index.html`):

```js
var DEFAULT_BASE = 'https://YOURSITE.smarterasp.net';
```

(Optional: to live-refine the headline stat, load the showcase with
`?tilesBase=https://pub-xxxx.r2.dev`. If you skip it, the stat shows the correct static value, 46M.)

---

## Phase 3 — Assemble wwwroot and publish the server (self-contained)

```powershell
# built app + showcase into the server's wwwroot (gitignored)
Remove-Item -Recurse -Force src/Colossus.Server/wwwroot -ErrorAction SilentlyContinue
New-Item -ItemType Directory src/Colossus.Server/wwwroot/showcase -Force | Out-Null
Copy-Item web/dist/* src/Colossus.Server/wwwroot/ -Recurse
Copy-Item showcase/*  src/Colossus.Server/wwwroot/showcase/ -Recurse

# publish with the runtime bundled (host needs no .NET 10 installed)
dotnet publish src/Colossus.Server -c Release -r win-x64 --self-contained true -o publish

# ship the view configs next to the app so /api/views works on the host
Copy-Item views publish/views -Recurse -Force
```

Then edit `publish/appsettings.Production.json` → set `Server:WebBaseUrl` to
`https://YOURSITE.smarterasp.net` (used only for the deep-link the picker hands out).

The publish output is a self-contained ASP.NET Core app: `Colossus.Server.exe`, `web.config`
(AspNetCoreModuleV2, **in-process** — the default, and required so the app respects IIS's port),
`appsettings*.json`, `wwwroot/`, `views/`, and the bundled runtime. No tiles here — they live on R2.

---

## Phase 4 — Deploy to SmarterASP.NET

1. Zip the **contents** of `publish/` (so `web.config` sits at the zip root).
2. SmarterASP.NET control panel → **File Manager** → your site root (the `wwwroot` folder they gave
   you) → upload the zip → **Extract**. (Or push the same files over FTP.)
3. **App pool**: set **.NET CLR version = "No Managed Code"** (ASP.NET Core is hosted by the native
   module, not the CLR pipeline). Set the pool to **64-bit** (Enable 32-bit Applications = **False**)
   to match `win-x64`.
4. Environment defaults to **Production**, so `appsettings.Production.json` loads automatically —
   nothing to set. (The `TilesRoot`/`views` folders now resolve relative to the app, no absolute
   paths needed.)
5. Browse to your site.

---

## Phase 5 — Smoke test live

- `https://YOURSITE.smarterasp.net/` → app loads, world map renders. In DevTools → Network, tile
  requests go to `R2_BASE` and return **200** (and **206** for `facts.pack?tile=…` ranged reads).
- `https://YOURSITE.smarterasp.net/showcase/` → showcase renders, headline reads 46M, the featured
  `mobile-dominance` map is interactive.
- `https://YOURSITE.smarterasp.net/api/views` → JSON list of the 4 views.
- **Remote fold** (only if a view priced `remote`, or to smoke the endpoint): a POST returns Arrow bytes and
  an `X-Fold-Ms` header. Any view with a retained `facts.parquet` answers, whatever its priced route:

  ```powershell
  curl -s -X POST "https://YOURSITE.smarterasp.net/api/views/mobile-dominance/fold" `
    -H "content-type: application/json" `
    -d '{\"measures\":[\"dominant_operator\"],\"context\":{\"equals\":{\"operator\":\"apex\"},\"ranges\":{}},\"tiles\":[\"5/17/19\"]}' `
    -o fold.arrow -D -
  ```

  A `400 … has no retained facts Parquet` means the view was baked before R4 (re-bake it); a
  `404 facts Parquet missing` means the file wasn't shipped next to the app (see the R4 note above).

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `500.30` / `500.31` / `502.5` on startup | Runtime/bitness mismatch. Self-contained + in-process avoids the runtime case; if the pool is 32-bit, either flip it to 64-bit or republish `-r win-x86`. |
| Blank map, CORS errors on tile requests | R2 bucket CORS missing your site origin or the `Range` header — re-check the Phase 1 CORS JSON. |
| `facts.pack` fails / group view won't recolor | The host must honor HTTP **Range**. R2 does natively; verify the request returns **206**, not 200. |
| Dataset picker empty (but maps still work) | `views/` wasn't shipped next to the app (Phase 3 copy). The map itself never needs it. |
| Headline stat not live-refining | Showcase `?tilesBase` / stat origin not pointed at R2. Harmless — static 46M still shows. |

## Rebaking later

A new bake writes a new `v…Z` version folder (with its `.arrow.br` siblings already in place) and updates
`latest.json`. To publish it: re-run the Phase 1 uploads — the plain immutable copy, the always-br `.arrow`
staging + copy ("Transport compression"), and the short-cache `latest.json`. The version flip invalidates
cleanly; no server redeploy needed unless the frontend changed.
