import type { SlabData } from './slabDecode';
import type { MeasureExpr, Agg } from '../measureGrammar';
import type { FoldContext } from '../foldContext';
import { axisIndex, resolveBins } from './slabAxes';
import { DenseAgg, argmaxFinalize, shareFinalize, plainFinalize } from '../aggregators';
import type { Folder } from './slabFolder';

// ── dense (cell-major, cumulative) ─────────────────────────────────────────────────────────────────────

export function foldDense(
  s: SlabData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  const M = markCount;
  const cumAi = s.axes.findIndex((a) => a.cumulative);
  const T = cumAi >= 0 ? s.axes[cumAi].cardinality : 1;
  const runs = s.cells / T; // categorical/non-cumulative positions (cumulative axis is innermost, stride 1)
  const dc = compileDenseContext(s, ctx, cumAi, T);

  const survived = new Uint8Array(M);
  const cnt = s.planes['cnt'];
  // A (run g, mark m) contributes iff it has ≥1 fact in the selected ordered range — the cumulative cnt.
  const rangeCnt = (g: number, m: number) => rangeCum(cnt, g, m, M, T, dc.lo, dc.hi);
  if (!dc.impossible && cnt)
    for (let g = 0; g < runs; g++) {
      if (!dc.runPass[g]) continue;
      for (let m = 0; m < M; m++) if (rangeCnt(g, m) > 0) survived[m] = 1;
    }

  const folders = measures.map((mm) => makeDenseFolder(mm.name, mm.ast, s, M, T, runs, dc, domains));
  const out: Record<string, Float32Array | Uint16Array> = {};
  for (const f of folders) out[f.name] = f.finalize(survived);
  return out;
}

interface DenseCtx {
  runPass: Uint8Array;
  lo: number;
  hi: number;
  impossible: boolean;
}

function compileDenseContext(s: SlabData, ctx: FoldContext, cumAi: number, T: number): DenseCtx {
  const runs = s.cells / T;
  const eqs: { stride: number; card: number; want: number }[] = [];
  const rgs: { stride: number; card: number; lo: number; hi: number }[] = [];
  let lo = 0;
  let hi = T - 1;
  let impossible = false;
  for (const ch in ctx.equals) {
    const ai = axisIndex(s.axes, ch);
    const want = ai >= 0 ? s.axes[ai].domain.indexOf(ctx.equals[ch]) : -1;
    if (want < 0) impossible = true;
    else eqs.push({ stride: s.strides[ai], card: s.axes[ai].cardinality, want });
  }
  for (const ch in ctx.ranges) {
    const ai = axisIndex(s.axes, ch);
    if (ai < 0 || s.axes[ai].kind !== 'ordered') { impossible = true; continue; }
    const [l, h] = resolveBins(s.axes[ai], ctx.ranges[ch].from, ctx.ranges[ch].to);
    if (l > h) impossible = true;
    else if (ai === cumAi) { lo = l; hi = h; }
    else rgs.push({ stride: s.strides[ai], card: s.axes[ai].cardinality, lo: l, hi: h });
  }
  const runPass = new Uint8Array(runs);
  if (!impossible)
    run: for (let g = 0; g < runs; g++) {
      const base = g * T; // cumulative axis (stride 1) coord 0
      for (const e of eqs) if (Math.floor(base / e.stride) % e.card !== e.want) continue run;
      for (const r of rgs) {
        const b = Math.floor(base / r.stride) % r.card;
        if (b < r.lo || b > r.hi) continue run;
      }
      runPass[g] = 1;
    }
  return { runPass, lo, hi, impossible };
}

/** Cumulative-plane range sum for (run g, mark m) over ordered bins [lo,hi]: two indexed reads. */
function rangeCum(plane: Float32Array, g: number, m: number, M: number, T: number, lo: number, hi: number): number {
  const base = g * T;
  const hiV = plane[(base + hi) * M + m];
  const loV = lo > 0 ? plane[(base + lo - 1) * M + m] : 0;
  return hiV - loV;
}

/** Raw-plane extremum for (run g, mark m) over [lo,hi] (min/max never cumulative — scan). */
function rangeScan(plane: Float32Array, g: number, m: number, M: number, T: number, lo: number, hi: number, isMax: boolean): number {
  const base = g * T;
  let best = isMax ? -Infinity : Infinity;
  for (let b = lo; b <= hi; b++) {
    const v = plane[(base + b) * M + m];
    if (Number.isNaN(v)) continue;
    best = isMax ? Math.max(best, v) : Math.min(best, v);
  }
  return best;
}

// The two partial planes a numeric agg reads (primary, secondary), for the dense range fold.
function aggPlaneNames(a: Agg): [string, string | null] {
  switch (a.kind) {
    case 'sum': return [`sum__${a.channel}`, null];
    case 'count': return ['cnt', null];
    case 'avg': return [`sum__${a.channel}`, 'cnt'];
    case 'wavg': return [`swp__${a.channel}__${a.weight}`, `sum__${a.weight}`];
    case 'min': return [`min__${a.channel}`, null];
    case 'max': return [`max__${a.channel}`, null];
  }
}

/** Accumulate one numeric agg over the dense slab for every passing (run, mark) into `agg`, keyed by
 *  `group(g,m)`. `restrict` gates a run by a categorical where-code. */
function denseAccumulate(
  inner: Agg,
  agg: DenseAgg,
  s: SlabData,
  M: number,
  T: number,
  runs: number,
  dc: DenseCtx,
  group: (g: number, m: number) => number,
  restrict?: (g: number) => boolean,
): void {
  if (dc.impossible) return;
  const [pName, sName] = aggPlaneNames(inner);
  const primary = s.planes[pName];
  const secondary = sName ? s.planes[sName] : null;
  const cnt = s.planes['cnt'];
  const minmax = inner.kind === 'min' || inner.kind === 'max';
  for (let g = 0; g < runs; g++) {
    if (!dc.runPass[g] || (restrict && !restrict(g))) continue;
    for (let m = 0; m < M; m++) {
      if (rangeCum(cnt, g, m, M, T, dc.lo, dc.hi) <= 0) continue; // no surviving fact in this (run,mark)
      const p = minmax
        ? rangeScan(primary, g, m, M, T, dc.lo, dc.hi, inner.kind === 'max')
        : rangeCum(primary, g, m, M, T, dc.lo, dc.hi);
      const sec = secondary ? rangeCum(secondary, g, m, M, T, dc.lo, dc.hi) : 0;
      agg.add(group(g, m), p, sec);
    }
  }
}

function makeDenseFolder(
  name: string,
  ast: MeasureExpr,
  s: SlabData,
  M: number,
  T: number,
  runs: number,
  dc: DenseCtx,
  domains: Record<string, string[]>,
): Folder {
  const axisCodeAt = (g: number, ai: number) => Math.floor((g * T) / s.strides[ai]) % s.axes[ai].cardinality;

  if (ast.kind === 'argmax' || ast.kind === 'argmin') {
    const dimAi = axisIndex(s.axes, ast.dimension);
    const axisDomain = dimAi >= 0 ? s.axes[dimAi].domain : [];
    const domain = domains[ast.dimension] ?? axisDomain;
    const d = domain.length || 1;
    const isMax = ast.kind === 'argmax';
    const lut = Int32Array.from(axisDomain, (v) => domain.indexOf(v));
    const agg = new DenseAgg(ast.inner.kind, M * d);
    denseAccumulate(ast.inner, agg, s, M, T, runs, dc, (g, m) => {
      const k = dimAi >= 0 ? lut[axisCodeAt(g, dimAi)] : -1;
      return k >= 0 ? m * d + k : M * d; // out-of-range sink group (never read)
    });
    return { name, finalize: (survived) => argmaxFinalize(agg, survived, M, d, isMax) };
  }
  if (ast.kind === 'share') {
    const whereAi = axisIndex(s.axes, ast.whereChannel);
    const want = whereAi >= 0 ? s.axes[whereAi].domain.indexOf(ast.whereValue) : -1;
    const restricted = new DenseAgg(ast.inner.kind, M);
    const unrestricted = new DenseAgg(ast.inner.kind, M);
    denseAccumulate(ast.inner, unrestricted, s, M, T, runs, dc, (_g, m) => m);
    denseAccumulate(ast.inner, restricted, s, M, T, runs, dc, (_g, m) => m, (g) => whereAi >= 0 && axisCodeAt(g, whereAi) === want);
    return { name, finalize: (survived) => shareFinalize(restricted, unrestricted, survived, M) };
  }
  const flat = ast as Agg;
  const hasWhere = !!flat.where;
  const whereAi = flat.where ? axisIndex(s.axes, flat.where.channel) : -1;
  const want = flat.where && whereAi >= 0 ? s.axes[whereAi].domain.indexOf(flat.where.value) : -1;
  const agg = new DenseAgg(flat.kind, M);
  denseAccumulate(flat, agg, s, M, T, runs, dc, (_g, m) => m, hasWhere ? (g) => whereAi >= 0 && axisCodeAt(g, whereAi) === want : undefined);
  return { name, finalize: (survived) => plainFinalize(agg, survived, M) };
}
