// Remote-fold routing benchmark (companion-scale R4). Measures the SAME fixed benchmark viewport on
// two routes so the R4 before (local client fold) and after (remote server fold) numbers come from one
// script and one method:
//   npx vite-node scripts/bench-fold-route.ts -- mobile-dominance                 # local baseline only
//   npx vite-node scripts/bench-fold-route.ts -- mobile-dominance --remote http://localhost:5173
//   npx vite-node scripts/bench-fold-route.ts -- mobile-dominance --remote <base> --parity
// The viewport is a fixed, dense z5 leaf block (Europe) recorded in docs/companion-scale/R4-BUILD.md.
// Local numbers read facts.pack off disk and run the REAL client fold (slab decode + foldSlab), exactly
// as the tile worker does. Remote numbers POST the context + tile keys to /api/views/{id}/fold and decode
// the Arrow the server returns. Latency is localhost-only (stated in the report); we report measured
// values, never WAN extrapolations.

import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import type { Manifest } from '../src/lib/manifest';
import { buildFoldContext, type FoldContext, type MeasureExpr, parseMeasure } from '../src/lib/measures';
import { decodeFoldResponse } from '../src/lib/remoteFold';
import { decodeSlab, foldSlab, slabPlanesForMeasures } from '../src/lib/slab';

const TILES = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tiles');

// The fixed benchmark viewport: a dense contiguous z5 (leaf) block over Europe. Recorded here and in
// R4-BUILD.md so before/after runs use the identical tile set.
const VIEWPORT: Record<string, string[]> = {
  'mobile-dominance': [
    '5/15/18', '5/15/19', '5/15/20',
    '5/16/18', '5/16/19', '5/16/20',
    '5/17/18', '5/17/19', '5/17/20',
    '5/18/18', '5/18/19', '5/18/20',
  ],
};

interface Args {
  view: string;
  remote?: string;
  parity: boolean;
}

function loadManifest(viewId: string): { manifest: Manifest; version: string; dir: string } {
  const latest = JSON.parse(readFileSync(join(TILES, viewId, 'latest.json'), 'utf8')) as { version: string };
  const dir = join(TILES, viewId, latest.version);
  const manifest = JSON.parse(readFileSync(join(dir, 'manifest.json'), 'utf8')) as Manifest;
  return { manifest, version: latest.version, dir };
}

const sum = (xs: number[]): number => xs.reduce((a, b) => a + b, 0);
const pct = (xs: number[], p: number): number => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))];
};
const isoOfDay = (day: number): string => new Date(day * 86400000).toISOString().slice(0, 10);
const dayOfIso = (s: string): number => Math.floor(Date.parse(`${s}T00:00:00Z`) / 86400000);

const measuresOf = (m: Manifest): { name: string; ast: MeasureExpr }[] =>
  (m.view.measures ?? []).map((mm) => ({ name: mm.name, ast: parseMeasure(mm.expr) }));

const argmaxDomains = (m: Manifest): Record<string, string[]> => {
  const out: Record<string, string[]> = {};
  for (const mm of m.view.measures ?? []) {
    const ast = parseMeasure(mm.expr);
    if ((ast.kind === 'argmax' || ast.kind === 'argmin') && m.channelDomains?.[ast.dimension]?.values)
      out[ast.dimension] = m.channelDomains[ast.dimension]!.values!;
  }
  return out;
};

/** The view's color measure — the archetypal single-measure recolor whose plane-split fetch R5 shrinks. */
function colorMeasureName(m: Manifest): string {
  const declared = m.view.encoding?.color?.channel;
  const names = new Set((m.view.measures ?? []).map((x) => x.name));
  return declared && names.has(declared) ? declared : (m.view.measures ?? [])[0]!.name;
}

/** ≥50 varied fold contexts: operator equality × quarter ranges (identical construction to bench-companion). */
function buildContexts(m: Manifest): Record<string, string>[] {
  const dims = m.view.source.channels.filter((c) => c.role === 'dimension').map((c) => c.name);
  const temporals = m.view.source.channels.filter((c) => c.role === 'temporal' || c.type === 'date').map((c) => c.name);
  const opCh = dims[0];
  const tCh = temporals[0];
  const ops = (opCh && m.channelDomains?.[opCh]?.values) || [];
  const dom = tCh ? m.channelDomains?.[tCh] : undefined;
  const vals = dom?.values ?? [];
  const lo = vals.length ? dayOfIso(vals[0]) : (dom?.min ?? 0);
  const hi = vals.length ? dayOfIso(vals[vals.length - 1]) : (dom?.max ?? 0);
  const span = Math.max(1, hi - lo);
  const N = 6;
  const at = (f: number) => isoOfDay(lo + Math.round(f * span));
  const ranges: string[] = [];
  for (let k = 1; k <= N; k++) ranges.push(`${at(0)}..${at(k / N)}`);
  for (let k = 0; k <= N - 2; k++) ranges.push(`${at(k / N)}..${at((k + 2) / N)}`);
  const ctxs: Record<string, string>[] = [];
  for (const op of ops) if (opCh) ctxs.push({ [opCh]: op });
  for (const r of ranges) if (tCh) ctxs.push({ [tCh]: r });
  for (const op of ops) for (const r of ranges) if (opCh && tCh) ctxs.push({ [opCh]: op, [tCh]: r });
  return ctxs;
}

// ── local route: decode the color-measure planes off facts.pack and foldSlab, as the tile worker does ──

interface LocalTile {
  key: string;
  markCount: number;
  planes: Record<string, ArrayBuffer>; // decompressed Arrow IPC per plane
  fetchedBytes: number; // compressed block bytes the plane-split fetch moved
}

function loadViewportLocal(manifest: Manifest, pack: Buffer, tiles: string[], measureNames: string[]): LocalTile[] {
  const markByKey = new Map(manifest.tiles.map((t) => [`${t.z}/${t.x}/${t.y}`, t.count]));
  const need = slabPlanesForMeasures(manifest, measureNames);
  const out: LocalTile[] = [];
  for (const key of tiles) {
    const dir = manifest.companionPack?.planeEntries?.[key];
    if (!dir) throw new Error(`no plane directory for ${key}`);
    const planes: Record<string, ArrayBuffer> = {};
    let fetched = 0;
    for (const p of need) {
      const rng = dir[p];
      if (!rng) continue;
      const [off, len] = rng;
      fetched += len;
      const g = gunzipSync(pack.subarray(off, off + len));
      planes[p] = g.buffer.slice(g.byteOffset, g.byteOffset + g.byteLength);
    }
    out.push({ key, markCount: markByKey.get(key) ?? 0, planes, fetchedBytes: fetched });
  }
  return out;
}

// ── remote route: POST the context + tiles, decode per-tile columns from the returned Arrow ──

interface RemoteResult {
  byTile: Map<string, Record<string, Float32Array | Uint16Array>>;
  responseBytes: number;
  serverMs: number | null;
}

async function foldRemote(
  base: string,
  viewId: string,
  version: string,
  measureNames: string[],
  ctx: FoldContext,
  tiles: string[],
): Promise<RemoteResult> {
  const res = await fetch(`${base}/api/views/${viewId}/fold`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ version, measures: measureNames, context: ctx, tiles }),
  });
  if (!res.ok) throw new Error(`fold ${res.status}: ${await res.text()}`);
  const buf = new Uint8Array(await res.arrayBuffer());
  const serverMs = res.headers.get('x-fold-ms');
  // Decoded through the REAL client decoder, so the harness measures the path the app runs.
  const { byTile } = decodeFoldResponse(buf, measureNames);
  return { byTile, responseBytes: buf.byteLength, serverMs: serverMs ? +serverMs : null };
}

// ── parity: byte-compare local vs remote folded columns (NaN treated equal to NaN) ──

function columnsEqual(a: Float32Array | Uint16Array, b: Float32Array | Uint16Array): number {
  if (a.length !== b.length) return -1;
  for (let i = 0; i < a.length; i++) {
    const av = a[i];
    const bv = b[i];
    if (Number.isNaN(av) && Number.isNaN(bv)) continue;
    if (av !== bv) return i;
  }
  return a.length; // all equal
}

async function main() {
  const argv = process.argv.slice(2);
  const view = argv.find((a) => !a.startsWith('--'))!;
  const remoteIdx = argv.indexOf('--remote');
  const args: Args = {
    view,
    remote: remoteIdx >= 0 ? argv[remoteIdx + 1] : undefined,
    parity: argv.includes('--parity'),
  };

  const { manifest, version } = loadManifest(view);
  const pack = readFileSync(join(TILES, view, version, manifest.companionPack!.file));
  const tiles = VIEWPORT[view];
  if (!tiles) throw new Error(`no benchmark viewport recorded for ${view}`);
  const measures = measuresOf(manifest);
  const domains = argmaxDomains(manifest);
  const colorMeasure = colorMeasureName(manifest);
  const active = measures.filter((m) => m.name === colorMeasure);
  const activeNames = active.map((m) => m.name);
  const ctxs = buildContexts(manifest);
  const markByKey = new Map(manifest.tiles.map((t) => [`${t.z}/${t.x}/${t.y}`, t.count]));
  const viewportMarks = sum(tiles.map((k) => markByKey.get(k) ?? 0));

  // ── LOCAL baseline ──
  const local = loadViewportLocal(manifest, pack, tiles, activeNames);
  const decoded = local.map((t) => ({ key: t.key, markCount: t.markCount, slab: decodeSlab(t.planes, manifest.companionSlab!) }));
  const localTransferBytes = sum(local.map((t) => t.fetchedBytes)); // color-measure planes for the viewport

  // Warm filter change: planes resident → foldSlab only (this is what a filter change costs on the local
  // route once the viewport's color-measure planes are cached — every subsequent context is fold-only).
  const foldViewport = (ctx: FoldContext): Record<string, Record<string, Float32Array | Uint16Array>> => {
    const r: Record<string, Record<string, Float32Array | Uint16Array>> = {};
    for (const t of decoded) r[t.key] = foldSlab(t.slab, active, ctx, t.markCount, domains);
    return r;
  };
  for (const ctx of ctxs) foldViewport(buildFoldContext(manifest.view, ctx)); // warm JIT
  for (const ctx of ctxs) foldViewport(buildFoldContext(manifest.view, ctx));
  const localFoldMs: number[] = [];
  for (const ctx of ctxs) {
    const fc = buildFoldContext(manifest.view, ctx);
    let best = Infinity;
    for (let r = 0; r < 3; r++) {
      const t0 = performance.now();
      foldViewport(fc);
      best = Math.min(best, performance.now() - t0);
    }
    localFoldMs.push(best);
  }

  const report: Record<string, unknown> = {
    view,
    version,
    format: `slab/${manifest.companionSlab!.layout}`,
    foldExecution: (manifest as unknown as { foldExecution?: string }).foldExecution ?? '(not recorded)',
    viewportTiles: tiles.length,
    viewportMarks,
    colorMeasure,
    contexts: ctxs.length,
    local: {
      transferBytesPerInteraction_coldColorPlanes: localTransferBytes,
      transferBytesPerInteraction_warmFilterChange: 0,
      foldMsP50: +pct(localFoldMs, 50).toFixed(4),
      foldMsP95: +pct(localFoldMs, 95).toFixed(4),
      note: 'warm filter change = foldSlab over resident planes; e2e per filter change == this (no fetch, no network).',
    },
  };

  // ── REMOTE route (optional) ──
  if (args.remote) {
    const base = args.remote;
    // warm up server + JIT
    for (const ctx of ctxs.slice(0, 5)) await foldRemote(base, view, version, activeNames, buildFoldContext(manifest.view, ctx), tiles);
    const e2e: number[] = [];
    const serverMs: number[] = [];
    const respBytes: number[] = [];
    let parityChecked = 0;
    let parityFail = 0;
    for (const ctx of ctxs) {
      const fc = buildFoldContext(manifest.view, ctx);
      const t0 = performance.now();
      const rr = await foldRemote(base, view, version, activeNames, fc, tiles);
      e2e.push(performance.now() - t0);
      respBytes.push(rr.responseBytes);
      if (rr.serverMs != null) serverMs.push(rr.serverMs);
      if (args.parity) {
        const localFolded = foldViewport(fc);
        for (const t of tiles) {
          const l = localFolded[t]?.[colorMeasure];
          const r = rr.byTile.get(t)?.[colorMeasure];
          parityChecked++;
          if (!l || !r || columnsEqual(l, r) !== l.length) {
            parityFail++;
            if (parityFail <= 5) {
              const idx = l && r ? columnsEqual(l, r) : -2;
              console.error(`PARITY FAIL ${t} ctx=${JSON.stringify(ctx)} at mki=${idx} local=${l?.[idx as number]} remote=${r?.[idx as number]} (lenL=${l?.length} lenR=${r?.length})`);
            }
          }
        }
      }
    }
    const marksXmeasuresX4 = viewportMarks * activeNames.length * 4;
    report.remote = {
      transferBytesPerInteraction_p50: +pct(respBytes, 50).toFixed(0),
      transferBytesPerInteraction_p95: +pct(respBytes, 95).toFixed(0),
      responseBytesSanity_marksXmeasuresX4: marksXmeasuresX4,
      serverFoldMsP50: serverMs.length ? +pct(serverMs, 50).toFixed(2) : 'not measured',
      serverFoldMsP95: serverMs.length ? +pct(serverMs, 95).toFixed(2) : 'not measured',
      e2eMsP50_localhost: +pct(e2e, 50).toFixed(2),
      e2eMsP95_localhost: +pct(e2e, 95).toFixed(2),
    };
    if (args.parity) report.parity = { checked: parityChecked, failed: parityFail, ok: parityFail === 0 };
  }

  console.log(JSON.stringify(report, null, 2));
}

await main();
