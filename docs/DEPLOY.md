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

   # immutable data: long cache
   rclone copy tiles r2:colossus-tiles --transfers 16 --checkers 16 `
     --header-upload "Cache-Control: public, max-age=31536000, immutable" `
     --exclude "*/latest.json"

   # pointer files: short cache (they change on every rebake)
   rclone copy tiles r2:colossus-tiles --include "*/latest.json" `
     --header-upload "Cache-Control: public, max-age=60"
   ```

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

---

## Phase 2 — Build the frontend (tiles → R2, API → same origin)

```powershell
cd web
$env:VITE_TILES_BASE = "https://pub-xxxx.r2.dev"   # your R2_BASE (no trailing slash)
$env:VITE_API_BASE   = "/api"                       # same-origin: the SmarterASP.NET app
npm run build                                       # -> web/dist
cd ..
```

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

A new bake writes a new `v…Z` version folder and updates `latest.json`. To publish it: re-run the
Phase 1 rclone upload (new version dir with immutable cache + `latest.json` with short cache). The
version flip invalidates cleanly; no server redeploy needed unless the frontend changed.
