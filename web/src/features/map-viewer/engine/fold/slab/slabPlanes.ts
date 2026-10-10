import { type Agg, type MeasureExpr, parseMeasure } from '../measureGrammar';
import { type Manifest, tileLayoutOf, type CompanionSlab } from '@/types/manifest';
import type { FoldContext } from '../foldContext';
import { computeStrides, resolveBins } from './slabAxes';

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

/** Plane names a set of measures needs on a tile of the given layout, plus `@idx` (sparse) and `cnt` (dense
 *  survival). Layout is per tile (SLAB-FORMAT §3), so this is resolved per tile. Used for the fold and, on
 *  the pack directory, for plane-split fetch. */
export function slabPlanesForMeasures(
  manifest: Manifest,
  measureNames: string[],
  layout: 'sparse' | 'dense' = manifest.companionSlab!.layout,
): string[] {
  const slab = manifest.companionSlab!;
  const byName = new Map((manifest.view.measures ?? []).map((m) => [m.name, m.expr]));
  const need = new Set<string>();
  for (const name of measureNames) {
    const expr = byName.get(name);
    if (expr) for (const p of measurePlanes(parseMeasure(expr))) need.add(p);
  }
  if (layout === 'sparse') need.add('@idx');
  else need.add('cnt'); // dense survival witness
  // Only planes the slab actually carries.
  const have = new Set(['@idx', ...slab.partials.map((p) => p.name)]);
  return [...need].filter((p) => have.has(p));
}

/** Bytes a plane-split fetch of these measures' planes moves for one tile (R5), under the tile's layout. */
export function activeFetchBytes(manifest: Manifest, tileKey: string, measureNames: string[]): number {
  const dir = manifest.companionPack?.planeEntries?.[tileKey];
  if (!dir) return 0;
  const layout = tileLayoutOf(manifest.companionSlab!, tileKey);
  let bytes = 0;
  for (const p of slabPlanesForMeasures(manifest, measureNames, layout)) bytes += dir[p]?.[1] ?? 0;
  return bytes;
}

// ── dense cell-run slicing (R5 second half) ─────────────────────────────────────────────────────────────

/** A raw (never cumulative) plane — the fold scans its `[lo..hi]` run rather than subtracting two rows. */
export const isScanPlane = (name: string): boolean => name.startsWith('min__') || name.startsWith('max__');

/** The cell rows a fold of `activePlanes` under `ctx` reads on a **dense** tile (SLAB-FORMAT §5), so the
 *  client fetches only those. Mirrors foldDense's reads exactly: for each passing categorical run, a
 *  cumulative plane needs the `hi` and (when `lo>0`) `lo−1` rows; a scan (min/max) plane needs the `[lo..hi]`
 *  run. `cnt` is among activePlanes (dense survival). `impossible` ⇒ the fold empties every mark, so nothing
 *  is fetched. With no cumulative axis (a degenerate view) every cell is needed (whole plane). */
export function denseNeeds(
  slab: CompanionSlab,
  ctx: FoldContext,
  activePlanes: string[],
): { cells: Record<string, number[]>; impossible: boolean } {
  const axes = slab.axes;
  const strides = computeStrides(axes);
  const cumAi = axes.findIndex((a) => a.cumulative);
  const T = cumAi >= 0 ? axes[cumAi].cardinality : 1;
  const runs = slab.cells / T;
  const ai = (ch: string) => axes.findIndex((a) => a.name === ch);

  const eqs: { stride: number; card: number; want: number }[] = [];
  const rgs: { stride: number; card: number; lo: number; hi: number }[] = [];
  let lo = 0;
  let hi = T - 1;
  let impossible = false;
  for (const ch in ctx.equals) {
    const i = ai(ch);
    const want = i >= 0 ? axes[i].domain.indexOf(ctx.equals[ch]) : -1;
    if (want < 0) impossible = true;
    else eqs.push({ stride: strides[i], card: axes[i].cardinality, want });
  }
  for (const ch in ctx.ranges) {
    const i = ai(ch);
    if (i < 0 || axes[i].kind !== 'ordered') { impossible = true; continue; }
    const [l, h] = resolveBins(axes[i], ctx.ranges[ch].from, ctx.ranges[ch].to);
    if (l > h) impossible = true;
    else if (i === cumAi) { lo = l; hi = h; }
    else rgs.push({ stride: strides[i], card: axes[i].cardinality, lo: l, hi: h });
  }

  const cells: Record<string, number[]> = {};
  for (const p of activePlanes) cells[p] = [];
  if (impossible) return { cells, impossible };
  if (cumAi < 0) {
    for (const p of activePlanes) cells[p] = [...Array(slab.cells).keys()];
    return { cells, impossible };
  }

  const cumRows = (base: number): number[] => (lo > 0 ? [base + hi, base + lo - 1] : [base + hi]);
  const scanRows = (base: number): number[] => {
    const r: number[] = [];
    for (let b = lo; b <= hi; b++) r.push(base + b);
    return r;
  };
  for (let g = 0; g < runs; g++) {
    const base = g * T; // this run's cumulative-axis coord 0 (the cumulative axis is innermost, stride 1)
    let pass = true;
    for (const e of eqs) if (Math.floor(base / e.stride) % e.card !== e.want) { pass = false; break; }
    if (pass) for (const r of rgs) { const b = Math.floor(base / r.stride) % r.card; if (b < r.lo || b > r.hi) { pass = false; break; } }
    if (!pass) continue;
    for (const p of activePlanes) for (const c of isScanPlane(p) ? scanRows(base) : cumRows(base)) cells[p].push(c);
  }
  return { cells, impossible };
}
