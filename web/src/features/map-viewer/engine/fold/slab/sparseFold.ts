import type { SlabData } from './slabDecode';
import type { FoldContext } from '../foldContext';
import { axisIndex, resolveBins } from './slabAxes';
import { type MeasureExpr, aggCh, aggW, type Agg } from '../measureGrammar';
import type { CompanionData } from '../companion';
import { InnerAgg, argmaxFinalize, shareFinalize, plainFinalize } from '../aggregators';
import type { Folder } from './slabFolder';

/** The active context compiled onto the slab's axes: a per-cell mask (SLAB-FORMAT §6). `impossible` marks
 *  a selection no fact can satisfy (channel/value absent) — every mark folds to unknown. */
function compileCellMask(s: SlabData, ctx: FoldContext): { mask: Uint8Array; impossible: boolean } {
  const eqs: { stride: number; card: number; want: number }[] = [];
  const rgs: { stride: number; card: number; lo: number; hi: number }[] = [];
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
    const r = ctx.ranges[ch];
    const [lo, hi] = resolveBins(s.axes[ai], r.from, r.to);
    if (lo > hi) impossible = true;
    else rgs.push({ stride: s.strides[ai], card: s.axes[ai].cardinality, lo, hi });
  }
  const mask = new Uint8Array(s.cells);
  if (!impossible)
    cell: for (let c = 0; c < s.cells; c++) {
      for (const e of eqs) if (Math.floor(c / e.stride) % e.card !== e.want) continue cell;
      for (const g of rgs) {
        const b = Math.floor(c / g.stride) % g.card;
        if (b < g.lo || b > g.hi) continue cell;
      }
      mask[c] = 1;
    }
  return { mask, impossible };
}

// ── sparse ──────────────────────────────────────────────────────────────────────────────────────────

export function foldSparse(
  s: SlabData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  const nnz = s.cellIds!.length;
  const cellIds = s.cellIds!;
  const offsets = s.offsets!;
  const pc = { partial: s.planes, rowCount: nnz } as unknown as CompanionData;
  const axisCode = (cellId: number, ai: number) => Math.floor(cellId / s.strides[ai]) % s.axes[ai].cardinality;

  const survived = new Uint8Array(markCount);
  const folders = measures.map((m) => makeSparseFolder(m.name, m.ast, s, pc, markCount, domains, axisCode));
  const { mask, impossible } = compileCellMask(s, ctx);

  if (!impossible)
    for (let m = 0; m < markCount; m++) {
      for (let e = offsets[m]; e < offsets[m + 1]; e++) {
        if (!mask[cellIds[e]]) continue;
        survived[m] = 1;
        for (const f of folders) f.add(m, e);
      }
    }
  const out: Record<string, Float32Array | Uint16Array> = {};
  for (const f of folders) out[f.name] = f.finalize(survived);
  return out;
}

interface SparseFolder extends Folder {
  add(m: number, e: number): void;
}

function makeSparseFolder(
  name: string,
  ast: MeasureExpr,
  s: SlabData,
  pc: CompanionData,
  n: number,
  domains: Record<string, string[]>,
  axisCode: (cellId: number, ai: number) => number,
): SparseFolder {
  const cellIds = s.cellIds!;
  if (ast.kind === 'argmax' || ast.kind === 'argmin') {
    const dimAi = axisIndex(s.axes, ast.dimension);
    const axisDomain = dimAi >= 0 ? s.axes[dimAi].domain : [];
    const domain = domains[ast.dimension] ?? axisDomain;
    const d = domain.length || 1;
    const isMax = ast.kind === 'argmax';
    const lut = Int32Array.from(axisDomain, (v) => domain.indexOf(v));
    const agg = new InnerAgg(ast.inner, pc, n * d, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(m, e) {
        const k = dimAi >= 0 ? lut[axisCode(cellIds[e], dimAi)] : -1;
        if (k >= 0) agg.add(m * d + k, e);
      },
      finalize: (survived) => argmaxFinalize(agg, survived, n, d, isMax),
    };
  }
  if (ast.kind === 'share') {
    const whereAi = axisIndex(s.axes, ast.whereChannel);
    const want = whereAi >= 0 ? s.axes[whereAi].domain.indexOf(ast.whereValue) : -1;
    const restricted = new InnerAgg(ast.inner, pc, n, aggCh(ast.inner), aggW(ast.inner));
    const unrestricted = new InnerAgg(ast.inner, pc, n, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(m, e) {
        unrestricted.add(m, e);
        if (whereAi >= 0 && axisCode(cellIds[e], whereAi) === want) restricted.add(m, e);
      },
      finalize: (survived) => shareFinalize(restricted, unrestricted, survived, n),
    };
  }
  const flat = ast as Agg;
  const hasWhere = !!flat.where;
  const whereAi = flat.where ? axisIndex(s.axes, flat.where.channel) : -1;
  const want = flat.where && whereAi >= 0 ? s.axes[whereAi].domain.indexOf(flat.where.value) : -1;
  const agg = new InnerAgg(flat, pc, n, aggCh(flat), aggW(flat));
  return {
    name,
    add(m, e) {
      if (!hasWhere) agg.add(m, e);
      else if (whereAi >= 0 && axisCode(cellIds[e], whereAi) === want) agg.add(m, e);
    },
    finalize: (survived) => plainFinalize(agg, survived, n),
  };
}
