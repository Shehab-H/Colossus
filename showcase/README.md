# Colossus — Showcase site

A standalone, dependency-free static page that presents the Colossus maps to an audience (e.g. a
demo for stakeholders). It embeds the live maps as `<iframe>`s in showcase-embed mode — filter and
color-by controls, no dataset switcher or app chrome.

It is **decoupled from the server**: it only needs the URL of a deployed Colossus web app. Host these
files anywhere static (nginx, S3, GitHub Pages, or the ASP.NET server's wwwroot).

## Point it at your deployment

The maps load from a single base origin — the Colossus **web app** (the React app that renders
`/?view=…&embed=1`), not the tiles server.

- **Permanent:** edit `DEFAULT_BASE` near the top of the `<script>` in `index.html`.
- **Per-visit:** append `?base=https://maps.example.com` to the showcase URL (no rebuild).

The headline "cells across datasets" stat ships as a correct static value and is refined from live
manifests when reachable. Tiles are often served from a different origin than the app (nginx / the
ASP.NET server) — point the stat at them with `?tilesBase=https://tiles.example.com`. If unreachable
it keeps the static value; nothing else on the page depends on it.

## What's shown

- A **featured** interactive map (`mobile-dominance`) with controls on.
- A **gallery** of locked snapshots (`controls=0`), each linking out to its full interactive map.

Edit the `VIEWS` array in `index.html` to change which maps appear, their blurbs, and their default
color measure.

## Local preview

With the web dev server running (e.g. `npm --prefix web run dev` on `:5173`):

```
# open index.html in a browser, or serve the folder:
npx serve showcase        # then visit the printed URL
```

If the dev server isn't on `:5173`, pass its origin: `…/showcase/?base=http://localhost:5173`.
