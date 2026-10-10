import type { CompanionData, CompanionDim } from './companion';
import { type MeasureExpr, aggCh, aggW, type Agg } from '../view/measureGrammar';
import { InnerAgg, ARGMAX_UNKNOWN } from './aggregators';
import type { FoldContext } from './foldContext';
import { parseNumericRange, MAX_SAFE_F32, dayNumberOfIso } from '../filters/filterValue';

// ── Fold engine ──────────────────────────────────────────────────────────────────────────────────
// Recompute each measure over a tile's fact partials under the active context. The partials are
// additive, so this is the same operation the bake did at the default context — only the surviving fact
// set differs. Mirror of the bake's default-context SQL (DuckDbFactGrouper), evaluated over companions.

// Must mirror gpuFilterSlots.MISSING_CODE without importing gpuFilterSlots (which depends on
// channelRoles → measureGrammar).
const NUMERIC_MISSING = Math.fround(2.0e38);

interface Folder {
  name: string;
  add(mi: number, row: number): void;
  finalize(survived: Uint8Array): Float32Array | Uint16Array;
}

/** Resolve a dimension literal to its per-row codes and the code to match: a value absent from the
 *  companion's dictionary matches nothing (want = -1; codes are ≥ 0). */
function resolveDim(c: CompanionData, channel: string, value: string): { codes: CompanionDim['codes']; want: number } {
  const d = c.dim[channel];
  return d ? { codes: d.codes, want: d.dict.indexOf(value) } : { codes: new Uint8Array(c.rowCount), want: -1 };
}

function makeFolder(
  name: string,
  ast: MeasureExpr,
  c: CompanionData,
  n: number,
  domains: Record<string, string[]>,
): Folder {
  if (ast.kind === 'argmax' || ast.kind === 'argmin') {
    const domain = domains[ast.dimension] ?? c.dim[ast.dimension]?.dict ?? [];
    const d = domain.length || 1;
    const isMax = ast.kind === 'argmax';
    // The companion's dictionary is written in the canonical order, so this LUT is normally the
    // identity — it only remaps if a bake and its manifest ever disagree.
    const dim = c.dim[ast.dimension];
    const lut = dim ? Int32Array.from(dim.dict, (v) => domain.indexOf(v)) : new Int32Array(0);
    const codes = dim?.codes ?? new Uint8Array(c.rowCount);
    const agg = new InnerAgg(ast.inner, c, n * d, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(mi, row) {
        const k = lut[codes[row]];
        if (k >= 0) agg.add(mi * d + k, row);
      },
      finalize(survived) {
        const out = new Uint16Array(n).fill(ARGMAX_UNKNOWN);
        for (let mi = 0; mi < n; mi++) {
          if (!survived[mi]) continue;
          let best = isMax ? -Infinity : Infinity;
          let bestCode = ARGMAX_UNKNOWN;
          for (let k = 0; k < d; k++) {
            const g = mi * d + k;
            if (!agg.has(g)) continue;
            const v = agg.value(g);
            if (Number.isNaN(v)) continue;
            if (isMax ? v > best : v < best) {
              best = v;
              bestCode = k;
            }
          }
          out[mi] = bestCode;
        }
        return out;
      },
    };
  }

  if (ast.kind === 'share') {
    const { codes, want } = resolveDim(c, ast.whereChannel, ast.whereValue);
    const restricted = new InnerAgg(ast.inner, c, n, aggCh(ast.inner), aggW(ast.inner));
    const unrestricted = new InnerAgg(ast.inner, c, n, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(mi, row) {
        unrestricted.add(mi, row);
        if (codes[row] === want) restricted.add(mi, row);
      },
      finalize(survived) {
        const out = new Float32Array(n);
        for (let mi = 0; mi < n; mi++) {
          if (!survived[mi]) {
            out[mi] = NaN;
            continue;
          }
          const u = unrestricted.value(mi);
          const r = restricted.value(mi);
          out[mi] = u ? (Number.isNaN(r) ? 0 : r) / u : NaN; // COALESCE(restricted,0) / nullif(whole,0)
        }
        return out;
      },
    };
  }

  // A plain aggregate, optionally restricted by its own `where`. (argmax/argmin/share returned above;
  // TS can't narrow the union's argmax|argmin member out through the early returns, hence the cast.)
  const flat = ast as Agg;
  const where = flat.where ? resolveDim(c, flat.where.channel, flat.where.value) : null;
  const agg = new InnerAgg(flat, c, n, aggCh(flat), aggW(flat));
  return {
    name,
    add(mi, row) {
      if (!where || where.codes[row] === where.want) agg.add(mi, row);
    },
    finalize(survived) {
      const out = new Float32Array(n);
      for (let mi = 0; mi < n; mi++) out[mi] = survived[mi] ? agg.value(mi) : NaN;
      return out;
    },
  };
}

/** The context compiled against one companion: per-row code equality tests + day-number range tests.
 *  `impossible` short-circuits the whole fold (a selection no fact can satisfy — every mark unknown). */
function compileContext(c: CompanionData, ctx: FoldContext) {
  const eqs: { codes: CompanionDim['codes']; want: number }[] = [];
  const rgs: { values: Float32Array; lo: number; hi: number; numeric: boolean }[] = [];
  let impossible = false;
  for (const ch in ctx.equals) {
    const r = resolveDim(c, ch, ctx.equals[ch]);
    if (r.want < 0) impossible = true;
    eqs.push(r);
  }
  for (const ch in ctx.ranges) {
    const numeric = c.numericValues?.[ch];
    const values = numeric ?? c.temporalDays[ch];
    if (!values) {
      impossible = true;
      continue;
    }
    const r = ctx.ranges[ch];
    const parsedNumeric = numeric ? parseNumericRange(`${r.from}..${r.to}`) : null;
    rgs.push({
      values,
      numeric: !!numeric,
      lo: numeric ? (parsedNumeric?.lower ?? -MAX_SAFE_F32) : r.from ? dayNumberOfIso(r.from) : -MAX_SAFE_F32,
      hi: numeric ? (parsedNumeric?.upper ?? MAX_SAFE_F32) : r.to ? dayNumberOfIso(r.to) : MAX_SAFE_F32,
    });
  }
  return { eqs, rgs, impossible };
}

/** Fold a tile's companion into per-mark measure values under the active context. Output arrays are
 *  indexed by the mark's row index in the render tile (the companion's `mki` column); numeric measures
 *  come back as Float32Array, argmax/argmin as Uint16Array of codes into the dimension's canonical
 *  domain. A mark with no surviving fact is NaN / ARGMAX_UNKNOWN — the unknown colour. The hot loop is
 *  integer compares and float adds over typed arrays; no strings, no hashing, no per-row allocation. */
export function foldTile(
  c: CompanionData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  const survived = new Uint8Array(markCount);
  const folders = measures.map((m) => makeFolder(m.name, m.ast, c, markCount, domains));
  const { eqs, rgs, impossible } = compileContext(c, ctx);
  const mki = c.mki;

  if (!impossible) {
    rows: for (let row = 0; row < c.rowCount; row++) {
      for (const e of eqs) if (e.codes[row] !== e.want) continue rows;
      for (const g of rgs) {
        const value = g.values[row];
        if (g.numeric && value === NUMERIC_MISSING) continue rows;
        if (value < g.lo || value > g.hi) continue rows;
      }
      const mi = mki[row];
      if (mi < 0 || mi >= markCount) continue;
      survived[mi] = 1;
      for (const f of folders) f.add(mi, row);
    }
  }

  const out: Record<string, Float32Array | Uint16Array> = {};
  for (const f of folders) out[f.name] = f.finalize(survived);
  return out;
}
