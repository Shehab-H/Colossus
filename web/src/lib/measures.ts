// Client mirror of the measure grammar (VIEW_CONFIG §4 / Colossus.Domain.Measures). Same AST, same
// errors, verified against the shared fixture tests/fixtures/measure-cases.json. The fold engine
// (foldTile, below) recomputes each measure over a tile's fact partials under the active context.

import type { ChannelSpec, ViewConfig } from './manifest';
import { type DateRange, MAX_SAFE_F32, dayNumberOfIso, parseDateRange } from './dates';

export interface WhereClause {
  channel: string;
  value: string;
}

export type Agg =
  | { kind: 'sum'; channel: string; where?: WhereClause }
  | { kind: 'count'; where?: WhereClause }
  | { kind: 'avg'; channel: string; where?: WhereClause }
  | { kind: 'wavg'; channel: string; weight: string; where?: WhereClause }
  | { kind: 'min'; channel: string; where?: WhereClause }
  | { kind: 'max'; channel: string; where?: WhereClause };

export type MeasureExpr =
  | Agg
  | { kind: 'share'; inner: Agg; whereChannel: string; whereValue: string }
  | { kind: 'argmax' | 'argmin'; dimension: string; inner: Agg };

const NUMERIC_VERBS = new Set(['sum', 'count', 'avg', 'wavg', 'min', 'max']);

type TokKind = 'ident' | 'str' | '(' | ')' | ',' | '=' | 'end';
interface Tok {
  kind: TokKind;
  text: string;
}
interface Pos {
  i: number;
}

const err = (message: string, expr: string): Error => new Error(`${message} in "${expr}"`);

export function parseMeasure(expr: string): MeasureExpr {
  const t = tokenize(expr);
  const p: Pos = { i: 0 };
  const result = parseExpr(t, p, expr);
  if (t[p.i].kind !== 'end') throw err(`unexpected trailing token '${t[p.i].text}'`, expr);
  return result;
}

function parseExpr(t: Tok[], p: Pos, expr: string): MeasureExpr {
  const verb = expectIdent(t, p, expr, 'an aggregate verb');
  if (verb === 'argmax' || verb === 'argmin') {
    expect(t, p, '(', expr);
    const dimension = expectIdent(t, p, expr, 'a dimension channel');
    expect(t, p, ',', expr);
    const inner = parseInnerAgg(t, p, expr);
    expect(t, p, ')', expr);
    return { kind: verb, dimension, inner };
  }
  if (verb === 'share') {
    expect(t, p, '(', expr);
    const inner = parseInnerAgg(t, p, expr);
    expect(t, p, ')', expr);
    const w = parseOptionalWhere(t, p, expr);
    if (!w) throw err("share(...) requires a 'where' clause", expr);
    return { kind: 'share', inner, whereChannel: w.channel, whereValue: w.value };
  }
  const agg = parseAggBody(verb, t, p, expr);
  const where = parseOptionalWhere(t, p, expr);
  return where ? { ...agg, where } : agg;
}

// An inner agg (argmax/share operand): one of the six numeric verbs, and it may not carry a where.
function parseInnerAgg(t: Tok[], p: Pos, expr: string): Agg {
  const verb = expectIdent(t, p, expr, 'an aggregate verb');
  if (!NUMERIC_VERBS.has(verb))
    throw err(`expected an aggregate verb (sum, count, avg, wavg, min, max), got '${verb}'`, expr);
  const agg = parseAggBody(verb, t, p, expr);
  if (t[p.i].kind === 'ident' && t[p.i].text === 'where')
    throw err("an inner aggregate may not carry a 'where' clause", expr);
  return agg;
}

function parseAggBody(verb: string, t: Tok[], p: Pos, expr: string): Agg {
  switch (verb) {
    case 'sum':
      return { kind: 'sum', channel: unary(t, p, expr) };
    case 'avg':
      return { kind: 'avg', channel: unary(t, p, expr) };
    case 'min':
      return { kind: 'min', channel: unary(t, p, expr) };
    case 'max':
      return { kind: 'max', channel: unary(t, p, expr) };
    case 'count':
      expect(t, p, '(', expr);
      expect(t, p, ')', expr);
      return { kind: 'count' };
    case 'wavg': {
      expect(t, p, '(', expr);
      const channel = expectIdent(t, p, expr, 'a numeric channel');
      expect(t, p, ',', expr);
      const weight = expectIdent(t, p, expr, 'a weight channel');
      expect(t, p, ')', expr);
      return { kind: 'wavg', channel, weight };
    }
    default:
      throw err(`unknown aggregate verb '${verb}'`, expr);
  }
}

function unary(t: Tok[], p: Pos, expr: string): string {
  expect(t, p, '(', expr);
  const ch = expectIdent(t, p, expr, 'a numeric channel');
  expect(t, p, ')', expr);
  return ch;
}

function parseOptionalWhere(t: Tok[], p: Pos, expr: string): WhereClause | null {
  if (t[p.i].kind !== 'ident' || t[p.i].text !== 'where') return null;
  p.i++;
  const channel = expectIdent(t, p, expr, 'a dimension channel');
  expect(t, p, '=', expr);
  const value = expectString(t, p, expr);
  return { channel, value };
}

function expectIdent(t: Tok[], p: Pos, expr: string, what: string): string {
  const tok = t[p.i];
  if (tok.kind !== 'ident') throw err(`expected ${what}, got '${tok.text}'`, expr);
  p.i++;
  return tok.text;
}

function expectString(t: Tok[], p: Pos, expr: string): string {
  const tok = t[p.i];
  if (tok.kind !== 'str') throw err(`expected a quoted literal, got '${tok.text}'`, expr);
  p.i++;
  return tok.text;
}

function expect(t: Tok[], p: Pos, kind: TokKind, expr: string): void {
  const tok = t[p.i];
  if (tok.kind !== kind) throw err(`expected '${kind}', got '${tok.text}'`, expr);
  p.i++;
}

// ── Fold engine ──────────────────────────────────────────────────────────────────────────────────
// Recompute each measure over a tile's fact partials under the active context. The partials are
// additive, so this is the same operation the bake did at the default context — only the surviving fact
// set differs. Mirror of the bake's default-context SQL (DuckDbFactGrouper), evaluated over companions.

/** One grain dimension of a companion: per-row codes into its small dictionary (written in the canonical
 *  order at bake). Codes stay integers through the whole fold — values only exist to resolve a context
 *  selection or a `where` literal to a code, once per fold. */
export interface CompanionDim {
  codes: Uint8Array | Uint16Array | Uint32Array;
  dict: string[];
}

/** A decoded fact companion, fully typed: `mki` keys each row to its mark's row index in the render
 *  tile (the bake writes it — no client-side string join), grain dimensions are dict codes, grain
 *  temporal values are day numbers, and partials are f32. Every array transfers across the worker
 *  boundary; nothing here allocates per-row strings. */
export interface CompanionData {
  rowCount: number;
  mki: Int32Array;
  dim: Record<string, CompanionDim>;
  temporalDays: Record<string, Float32Array>;
  partial: Record<string, Float32Array>;
}

/** The active fold context: perFact equality selections + temporal ranges (VIEW_CONFIG §1). */
export interface FoldContext {
  equals: Record<string, string>;
  ranges: Record<string, DateRange>;
}

/** Argmax code for a mark with no surviving facts — past any real domain, so the colour LUT paints it
 *  the unknown colour, exactly like the empty-set numeric NaN. */
export const ARGMAX_UNKNOWN = 0xffff;

type Numeric = { kind: 'sum' | 'count' | 'avg' | 'wavg' | 'min' | 'max' };

/** Accumulates one numeric inner-agg over a set of groups (a group is a mark, or a (mark, dim) pair for
 *  argmax, or restricted/unrestricted halves for share). Additive partials in, finalized value out. */
class InnerAgg {
  private readonly a: Float64Array;
  private readonly b: Float64Array | null;
  private readonly seen: Uint8Array;
  private readonly p: Float32Array;
  private readonly p2: Float32Array | null;
  private readonly inner: Numeric;

  constructor(inner: Numeric, c: CompanionData, groups: number, ch?: string, w?: string) {
    this.inner = inner;
    this.seen = new Uint8Array(groups);
    const get = (n: string): Float32Array => c.partial[n] ?? new Float32Array(c.rowCount);
    switch (inner.kind) {
      case 'sum':
        this.p = get(`sum__${ch}`);
        this.a = new Float64Array(groups);
        this.b = null;
        this.p2 = null;
        break;
      case 'count':
        this.p = get('cnt');
        this.a = new Float64Array(groups);
        this.b = null;
        this.p2 = null;
        break;
      case 'avg':
        this.p = get(`sum__${ch}`);
        this.p2 = get('cnt');
        this.a = new Float64Array(groups);
        this.b = new Float64Array(groups);
        break;
      case 'wavg':
        this.p = get(`swp__${ch}__${w}`);
        this.p2 = get(`sum__${w}`);
        this.a = new Float64Array(groups);
        this.b = new Float64Array(groups);
        break;
      case 'min':
        this.p = get(`min__${ch}`);
        this.a = new Float64Array(groups).fill(Infinity);
        this.b = null;
        this.p2 = null;
        break;
      case 'max':
        this.p = get(`max__${ch}`);
        this.a = new Float64Array(groups).fill(-Infinity);
        this.b = null;
        this.p2 = null;
        break;
    }
  }

  add(g: number, row: number): void {
    this.seen[g] = 1;
    switch (this.inner.kind) {
      case 'sum':
      case 'count':
      case 'avg':
      case 'wavg':
        this.a[g] += this.p[row];
        if (this.b) this.b[g] += this.p2![row];
        break;
      case 'min':
        this.a[g] = Math.min(this.a[g], this.p[row]);
        break;
      case 'max':
        this.a[g] = Math.max(this.a[g], this.p[row]);
        break;
    }
  }

  has(g: number): boolean {
    return this.seen[g] === 1;
  }

  /** Finalized value; additive aggs default to 0 when unseen (an empty sum is 0), the rest to NaN. */
  value(g: number): number {
    switch (this.inner.kind) {
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

const aggCh = (a: Agg): string | undefined => ('channel' in a ? a.channel : undefined);
const aggW = (a: Agg): string | undefined => (a.kind === 'wavg' ? a.weight : undefined);

/** The context compiled against one companion: per-row code equality tests + day-number range tests.
 *  `impossible` short-circuits the whole fold (a selection no fact can satisfy — every mark unknown). */
function compileContext(c: CompanionData, ctx: FoldContext) {
  const eqs: { codes: CompanionDim['codes']; want: number }[] = [];
  const rgs: { days: Float32Array; lo: number; hi: number }[] = [];
  let impossible = false;
  for (const ch in ctx.equals) {
    const r = resolveDim(c, ch, ctx.equals[ch]);
    if (r.want < 0) impossible = true;
    eqs.push(r);
  }
  for (const ch in ctx.ranges) {
    const days = c.temporalDays[ch];
    if (!days) {
      impossible = true;
      continue;
    }
    const r = ctx.ranges[ch];
    rgs.push({
      days,
      lo: r.from ? dayNumberOfIso(r.from) : -MAX_SAFE_F32,
      hi: r.to ? dayNumberOfIso(r.to) : MAX_SAFE_F32,
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
        const day = g.days[row];
        if (day < g.lo || day > g.hi) continue rows;
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

/** Build the fold context from the active perFact filter selections: temporal channels become ranges,
 *  the rest equality selections. */
export function buildFoldContext(view: ViewConfig, context: Record<string, string>): FoldContext {
  const byName = new Map<string, ChannelSpec>(view.source.channels.map((ch) => [ch.name, ch]));
  const equals: Record<string, string> = {};
  const ranges: Record<string, DateRange> = {};
  for (const [name, v] of Object.entries(context)) {
    const ch = byName.get(name);
    if (!ch) continue;
    if (ch.role === 'temporal' || ch.type === 'date') {
      const r = parseDateRange(v);
      if (r) ranges[name] = r;
    } else {
      equals[name] = v;
    }
  }
  return { equals, ranges };
}

function tokenize(expr: string): Tok[] {
  const tokens: Tok[] = [];
  let i = 0;
  while (i < expr.length) {
    const c = expr[i];
    if (/\s/.test(c)) {
      i++;
      continue;
    }
    if (c === '(' || c === ')' || c === ',' || c === '=') {
      tokens.push({ kind: c, text: c });
      i++;
      continue;
    }
    if (c === "'") {
      const start = ++i;
      while (i < expr.length && expr[i] !== "'") i++;
      if (i >= expr.length) throw err('unterminated string literal', expr);
      tokens.push({ kind: 'str', text: expr.slice(start, i) });
      i++; // closing quote
      continue;
    }
    if (/[A-Za-z_]/.test(c)) {
      const start = i;
      while (i < expr.length && /[A-Za-z0-9_]/.test(expr[i])) i++;
      tokens.push({ kind: 'ident', text: expr.slice(start, i) });
      continue;
    }
    throw err(`unexpected character '${c}'`, expr);
  }
  tokens.push({ kind: 'end', text: '<end>' });
  return tokens;
}
