// Client slab reader + fold (docs/companion-scale/SLAB-FORMAT.md), the R1 counterpart to the row-form
// decodeCompanion/foldTile. Same frozen fold results (VIEW_CONFIG §4) — only the traversal differs:
// sparse CSR scans a per-cell predicate (reusing measures.ts InnerAgg for the accumulation +
// finalization); dense reads cumulative cell rows in O(1) per range. The manifest's `companionSlab` gates
// which path runs; the row form stays for older bakes.

import { type Table, tableFromIPC } from 'apache-arrow';
import type { CompanionSlab, Manifest, SlabAxis } from './manifest';
import {
  type Agg,
  aggCh,
  aggW,
  ARGMAX_UNKNOWN,
  type CompanionData,
  type FoldContext,
  InnerAgg,
  type MeasureExpr,
  parseMeasure,
} from './measures';

export const isSlab = (m: Manifest): boolean => !!m.companionSlab;

type CellIds = Uint8Array | Uint16Array | Uint32Array;

/** A decoded slab tile: sparse carries `offsets`+`cellIds` plus per-partial planes over `nnz` entries;
 *  dense carries only cell-major (cumulative) planes over `cells × markCount`. Planes are Float32 (a cnt
 *  plane is widened from i32 at decode — counts are exact in f32). `strides` are the cell-order strides. */
export interface SlabData {
  layout: 'sparse' | 'dense';
  markCount: number;
  cells: number;
  axes: SlabAxis[];
  strides: number[];
  offsets?: Int32Array;
  cellIds?: CellIds;
  planes: Record<string, Float32Array>;
  decodedBytes: number;
}

/** Cell-order strides (fastest/last axis = stride 1). */
function computeStrides(axes: SlabAxis[]): number[] {
  const strides = new Array<number>(axes.length);
  let s = 1;
  for (let i = axes.length - 1; i >= 0; i--) {
    strides[i] = s;
    s *= axes[i].cardinality;
  }
  return strides;
}

/** The single-row List column's child typed array, viewed in place (zero-copy). */
function listChild(table: Table, name: string): Float32Array | Int32Array | CellIds {
  const col = table.getChild(name);
  if (!col) throw new Error(`slab block missing column '${name}'`);
  const d = col.data[0] as unknown as { valueOffsets: Int32Array; children: { values: Float32Array | Int32Array | CellIds }[] };
  const base = d.valueOffsets[0];
  const n = d.valueOffsets[1] - base;
  return d.children[0].values.subarray(base, base + n);
}

/** Decode a slab tile from a map of `planeName → decompressed Arrow IPC bytes`. Only the planes present in
 *  the map are decoded — plane splitting hands in `@idx` (sparse) plus just the active measures' planes. */
export function decodeSlab(planeBlocks: Record<string, ArrayBuffer>, slab: CompanionSlab): SlabData {
  const strides = computeStrides(slab.axes);
  let offsets: Int32Array | undefined;
  let cellIds: CellIds | undefined;
  let bytes = 0;
  if (slab.layout === 'sparse') {
    const idx = tableFromIPC(new Uint8Array(planeBlocks['@idx']));
    offsets = listChild(idx, 'offsets') as Int32Array;
    cellIds = listChild(idx, 'cellIds') as CellIds;
    bytes += offsets.byteLength + cellIds.byteLength;
  }
  const planes: Record<string, Float32Array> = {};
  for (const p of slab.partials) {
    const blk = planeBlocks[p.name];
    if (!blk) continue;
    const child = listChild(tableFromIPC(new Uint8Array(blk)), p.name);
    planes[p.name] = p.type === 'i32' ? Float32Array.from(child as Int32Array) : (child as Float32Array);
    bytes += planes[p.name].byteLength;
  }
  const first = Object.values(planes)[0];
  const markCount = slab.layout === 'sparse' ? (offsets!.length - 1) : first ? first.length / slab.cells : 0;
  return { layout: slab.layout, markCount, cells: slab.cells, axes: slab.axes, strides, offsets, cellIds, planes, decodedBytes: bytes };
}

// ── plane selection (fold needs / plane split fetches) ─────────────────────────────────────────────────

/** The partial planes an aggregate reads (mirrors the bake's MeasurePartials). */
function aggPlanes(a: Agg): string[] {
  switch (a.kind) {
    case 'sum': return [`sum__${a.channel}`];
    case 'count': return ['cnt'];
    case 'avg': return [`sum__${a.channel}`, 'cnt'];
    case 'wavg': return [`swp__${a.channel}__${a.weight}`, `sum__${a.weight}`];
    case 'min': return [`min__${a.channel}`];
    case 'max': return [`max__${a.channel}`];
  }
}

function measurePlanes(ast: MeasureExpr): string[] {
  if (ast.kind === 'argmax' || ast.kind === 'argmin' || ast.kind === 'share') return aggPlanes(ast.inner);
  return aggPlanes(ast as Agg);
}

/** Plane names a set of measures needs, plus `@idx` (sparse) and `cnt` (dense survival). Used for the
 *  fold and, on the pack directory, for plane-split fetch. */
export function slabPlanesForMeasures(manifest: Manifest, measureNames: string[]): string[] {
  const slab = manifest.companionSlab!;
  const byName = new Map((manifest.view.measures ?? []).map((m) => [m.name, m.expr]));
  const need = new Set<string>();
  for (const name of measureNames) {
    const expr = byName.get(name);
    if (expr) for (const p of measurePlanes(parseMeasure(expr))) need.add(p);
  }
  if (slab.layout === 'sparse') need.add('@idx');
  else need.add('cnt'); // dense survival witness
  // Only planes the slab actually carries.
  const have = new Set(['@idx', ...slab.partials.map((p) => p.name)]);
  return [...need].filter((p) => have.has(p));
}

/** Bytes a plane-split fetch of these measures' planes moves for one tile (R5). */
export function activeFetchBytes(manifest: Manifest, tileKey: string, measureNames: string[]): number {
  const dir = manifest.companionPack?.planeEntries?.[tileKey];
  if (!dir) return 0;
  let bytes = 0;
  for (const p of slabPlanesForMeasures(manifest, measureNames)) bytes += dir[p]?.[1] ?? 0;
  return bytes;
}

// ── fold ────────────────────────────────────────────────────────────────────────────────────────────

const axisIndex = (axes: SlabAxis[], channel: string): number => axes.findIndex((a) => a.name === channel);

/** Resolve an ordered range to [loBin, hiBin] over the axis's sorted domain (chronological == lexical on
 *  ISO). loBin > hiBin ⇒ empty. Open bounds span the full axis. */
function resolveBins(domain: string[], from: string, to: string): [number, number] {
  let lo = 0;
  let hi = domain.length - 1;
  if (from) while (lo < domain.length && domain[lo] < from) lo++;
  if (to) while (hi >= 0 && domain[hi] > to) hi--;
  return [lo, hi];
}

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
    const [lo, hi] = resolveBins(s.axes[ai].domain, r.from, r.to);
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

interface Folder {
  name: string;
  finalize(survived: Uint8Array): Float32Array | Uint16Array;
}

/** Fold a slab tile under the active context → per-mark measure columns (indexed by mki), byte-identical to
 *  foldTile. Sparse scans entries against the cell mask; dense reads cumulative cell rows. */
export function foldSlab(
  s: SlabData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  return s.layout === 'sparse'
    ? foldSparse(s, measures, ctx, markCount, domains)
    : foldDense(s, measures, ctx, markCount, domains);
}

// ── sparse ──────────────────────────────────────────────────────────────────────────────────────────

function foldSparse(
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

// ── dense (cell-major, cumulative) ─────────────────────────────────────────────────────────────────────

function foldDense(
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
    const [l, h] = resolveBins(s.axes[ai].domain, ctx.ranges[ch].from, ctx.ranges[ch].to);
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

/** A dense inner-agg over `groups`, fed range-folded (g,m) contributions. Value/finalization mirror
 *  measures.ts InnerAgg (frozen) — the fixture pins it byte-identical. */
class DenseAgg {
  private readonly a: Float64Array;
  private readonly b: Float64Array | null;
  private readonly seen: Uint8Array;
  private readonly kind: Agg['kind'];
  constructor(kind: Agg['kind'], groups: number) {
    this.kind = kind;
    this.seen = new Uint8Array(groups);
    this.a = new Float64Array(groups);
    this.b = kind === 'avg' || kind === 'wavg' ? new Float64Array(groups) : null;
    if (kind === 'min') this.a.fill(Infinity);
    if (kind === 'max') this.a.fill(-Infinity);
  }
  add(g: number, primary: number, secondary: number): void {
    this.seen[g] = 1;
    if (this.kind === 'min') this.a[g] = Math.min(this.a[g], primary);
    else if (this.kind === 'max') this.a[g] = Math.max(this.a[g], primary);
    else {
      this.a[g] += primary;
      if (this.b) this.b[g] += secondary;
    }
  }
  has(g: number): boolean { return this.seen[g] === 1; }
  value(g: number): number {
    switch (this.kind) {
      case 'sum':
      case 'count':
        return this.a[g];
      case 'avg':
      case 'wavg':
        return this.seen[g] && this.b![g] ? this.a[g] / this.b![g] : NaN;
      case 'min':
      case 'max':
        return this.seen[g] ? this.a[g] : NaN;
    }
  }
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

// ── shared finalization (identical for sparse InnerAgg and dense DenseAgg) ──────────────────────────────

interface Valued {
  has(g: number): boolean;
  value(g: number): number;
}

function plainFinalize(agg: Valued, survived: Uint8Array, n: number): Float32Array {
  const out = new Float32Array(n);
  for (let m = 0; m < n; m++) out[m] = survived[m] ? agg.value(m) : NaN;
  return out;
}

function shareFinalize(restricted: Valued, unrestricted: Valued, survived: Uint8Array, n: number): Float32Array {
  const out = new Float32Array(n);
  for (let m = 0; m < n; m++) {
    if (!survived[m]) {
      out[m] = NaN;
      continue;
    }
    const u = unrestricted.value(m);
    const r = restricted.value(m);
    out[m] = u ? (Number.isNaN(r) ? 0 : r) / u : NaN;
  }
  return out;
}

function argmaxFinalize(agg: Valued, survived: Uint8Array, n: number, d: number, isMax: boolean): Uint16Array {
  const out = new Uint16Array(n).fill(ARGMAX_UNKNOWN);
  for (let m = 0; m < n; m++) {
    if (!survived[m]) continue;
    let best = isMax ? -Infinity : Infinity;
    let bestCode = ARGMAX_UNKNOWN;
    for (let k = 0; k < d; k++) {
      const g = m * d + k;
      if (!agg.has(g)) continue;
      const v = agg.value(g);
      if (Number.isNaN(v)) continue;
      if (isMax ? v > best : v < best) {
        best = v;
        bestCode = k;
      }
    }
    out[m] = bestCode;
  }
  return out;
}
