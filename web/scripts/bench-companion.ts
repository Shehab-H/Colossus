// Companion-scale benchmark harness (R1). Measures the REAL client fold path — the same
// decodeCompanion / foldTile the app runs — over the REAL baked artifacts on disk, so the R1 before
// (row form) and after (slab) numbers come from one script. Run with vite-node:
//   npx vite-node scripts/bench-companion.ts -- <viewId> [<viewId> ...]
// Reads tiles/<view>/latest.json → manifest.json, ranges leaf blocks out of facts.pack exactly as the
// worker does, and reports bytes at rest + decode/fold cost on the worst leaf tile. Format-agnostic:
// a slab manifest is measured through the slab decode/fold (dynamically imported) instead of the row form.

import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { tableFromIPC } from 'apache-arrow';
import type { Manifest } from '../src/lib/manifest';
import { companionGrain, decodeCompanion } from '../src/lib/tileData';
import { buildFoldContext, type CompanionData, foldTile, type MeasureExpr, parseMeasure } from '../src/lib/measures';

const TILES = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'tiles');

interface Leaf {
  key: string;
  offset: number;
  length: number;
}

// Present iff the leaf pack carries per-plane byte ranges (R1 slab + R5 plane split).
const isSlab = (m: Manifest): boolean => (m.companionPack as { format?: string } | undefined)?.format === 'slab';

function loadManifest(viewId: string): { manifest: Manifest; version: string; dir: string } {
  const latest = JSON.parse(readFileSync(join(TILES, viewId, 'latest.json'), 'utf8')) as { version: string };
  const dir = join(TILES, viewId, latest.version);
  const manifest = JSON.parse(readFileSync(join(dir, 'manifest.json'), 'utf8')) as Manifest;
  return { manifest, version: latest.version, dir };
}

const leaves = (m: Manifest): Leaf[] => {
  const e = m.companionPack?.entries ?? {};
  return Object.entries(e).map(([key, [offset, length]]) => ({ key, offset, length }));
};

/** Sum of decompressed (raw Arrow) bytes across every leaf block — bytes at rest before pack gzip. */
function rawLeafBytes(pack: Buffer, ls: Leaf[]): number {
  let raw = 0;
  for (const l of ls) raw += gunzipSync(pack.subarray(l.offset, l.offset + l.length)).byteLength;
  return raw;
}

/** Decoded (in-memory) byte footprint of a row-form companion — the typed arrays the fold reads. */
function decodedBytes(c: CompanionData): number {
  let b = c.mki.byteLength;
  for (const d of Object.values(c.dim)) b += d.codes.byteLength;
  for (const t of Object.values(c.temporalDays)) b += t.byteLength;
  for (const p of Object.values(c.partial)) b += p.byteLength;
  return b;
}

const pct = (xs: number[], p: number): number => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))];
};

const isoOfDay = (day: number): string => new Date(day * 86400000).toISOString().slice(0, 10);
const dayOfIso = (s: string): number => Math.floor(Date.parse(`${s}T00:00:00Z`) / 86400000);

/** ≥50 varied fold contexts: operator equality × quarter ranges (VIEW_CONFIG §1 fold contexts). The
 *  temporal domain only records [min,max], so ranges are synthesized as a grid across that span —
 *  cumulative-from-start and sliding windows — which the day-number fold restricts to whichever bins fall
 *  inside. Deterministic, so before/after runs see the identical context set. */
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
  for (let k = 1; k <= N; k++) ranges.push(`${at(0)}..${at(k / N)}`); // cumulative from start
  for (let k = 0; k <= N - 2; k++) ranges.push(`${at(k / N)}..${at((k + 2) / N)}`); // sliding windows
  const ctxs: Record<string, string>[] = [{}];
  for (const op of ops) if (opCh) ctxs.push({ [opCh]: op });
  for (const r of ranges) if (tCh) ctxs.push({ [tCh]: r });
  for (const op of ops) for (const r of ranges) if (opCh && tCh) ctxs.push({ [opCh]: op, [tCh]: r });
  return ctxs;
}

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
function colorMeasureName(m: Manifest): string | undefined {
  const declared = m.view.encoding?.color?.channel;
  const names = new Set((m.view.measures ?? []).map((x) => x.name));
  return declared && names.has(declared) ? declared : (m.view.measures ?? [])[0]?.name;
}

async function bench(viewId: string) {
  const { manifest, version, dir } = loadManifest(viewId);
  const pack = readFileSync(join(dir, manifest.companionPack!.file));
  const ls = leaves(manifest);
  const grain = companionGrain(manifest);
  const measures = measuresOf(manifest);
  const domains = argmaxDomains(manifest);
  const markByKey = new Map(manifest.tiles.map((t) => [`${t.z}/${t.x}/${t.y}`, t.count]));
  const slab = isSlab(manifest);
  const slabLib = slab ? await import('../src/lib/slab') : null;

  const packedTotal = ls.reduce((a, l) => a + l.length, 0);
  const rawTotal = rawLeafBytes(pack, ls);
  const worst = ls.reduce((a, b) => (b.length > a.length ? b : a));
  const worstRaw = gunzipSync(pack.subarray(worst.offset, worst.offset + worst.length)).byteLength;

  // decode: gunzip + Arrow parse + decode into typed arrays. Returns the decoded companion (row or slab)
  // and its in-memory byte footprint.
  const decodeOne = (): { c: unknown; decoded: number } => {
    const raw = gunzipSync(pack.subarray(worst.offset, worst.offset + worst.length));
    if (slabLib) {
      const s = slabLib.decodeSlab(raw, manifest);
      return { c: s, decoded: s.decodedBytes };
    }
    const c = decodeCompanion(tableFromIPC(raw), grain);
    return { c, decoded: decodedBytes(c) };
  };

  const dt: number[] = [];
  let decoded = 0;
  let comp: unknown = null;
  for (let i = 0; i < 7; i++) {
    const t0 = performance.now();
    const r = decodeOne();
    dt.push(performance.now() - t0);
    decoded = r.decoded;
    comp = r.c;
  }
  const markCount = markByKey.get(worst.key) ?? 0;

  const ctxs = buildContexts(manifest);
  const foldOnce = (ctx: Record<string, string>): number => {
    const fc = buildFoldContext(manifest.view, ctx);
    const t0 = performance.now();
    if (slabLib) slabLib.foldSlab(comp as never, measures, fc, markCount, domains);
    else foldTile(comp as CompanionData, measures, fc, markCount, domains);
    return performance.now() - t0;
  };
  for (const ctx of ctxs) foldOnce(ctx); // warm up JIT
  for (const ctx of ctxs) foldOnce(ctx);
  const fold: number[] = [];
  for (const ctx of ctxs) {
    let best = Infinity;
    for (let r = 0; r < 3; r++) best = Math.min(best, foldOnce(ctx));
    fold.push(best);
  }

  // active-measure fetch: a slab fetches the color measure's planes (+ struct); the row form fetches all.
  const cm = colorMeasureName(manifest);
  let activeFetch = worst.length;
  if (slabLib && cm) activeFetch = slabLib.activeFetchBytes(manifest, worst.key, [cm]);

  return {
    view: viewId,
    version,
    format: slab ? 'slab' : 'row',
    leaves: ls.length,
    leafPackedTotal: packedTotal,
    leafRawTotal: rawTotal,
    worstTile: worst.key,
    worstPacked: worst.length,
    worstRaw,
    worstMarks: markCount,
    worstActiveMeasure: cm,
    worstActiveFetch: activeFetch,
    decodePrepMsMedian: +pct(dt, 50).toFixed(2),
    peakDecodedBytes: decoded,
    foldContexts: ctxs.length,
    foldMsP50: +pct(fold, 50).toFixed(4),
    foldMsP95: +pct(fold, 95).toFixed(4),
  };
}

const views = process.argv.slice(2).filter((a) => !a.startsWith('-'));
const out = [];
for (const v of views) out.push(await bench(v));
console.log(JSON.stringify(out, null, 2));
