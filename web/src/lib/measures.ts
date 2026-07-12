// Client mirror of the measure grammar (VIEW_CONFIG §4 / Colossus.Domain.Measures). Same AST, same
// errors, verified against the shared fixture tests/fixtures/measure-cases.json. The fold engine
// (foldTile, below) recomputes each measure over a tile's fact partials under the active context.

import type { ChannelSpec, ViewConfig } from './manifest';
import { type DateRange, inDateRange, parseDateRange } from './dates';

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

/** A decoded fact companion: per-row grain values (dict channels as strings, temporal as YYYY-MM-DD)
 *  and partial columns, plus the mark key each row folds into. */
export interface CompanionData {
  rowCount: number;
  mk: string[];
  dim: Record<string, string[]>;
  temporal: Record<string, string[]>;
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

const matches = (c: CompanionData, where: WhereClause | undefined, row: number): boolean =>
  !where || c.dim[where.channel]?.[row] === where.value;

function makeFolder(
  name: string,
  ast: MeasureExpr,
  c: CompanionData,
  n: number,
  domains: Record<string, string[]>,
): Folder {
  if (ast.kind === 'argmax' || ast.kind === 'argmin') {
    const domain = domains[ast.dimension] ?? [];
    const code = new Map(domain.map((v, i) => [v, i]));
    const d = domain.length || 1;
    const isMax = ast.kind === 'argmax';
    const agg = new InnerAgg(ast.inner, c, n * d, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(mi, row) {
        const k = code.get(c.dim[ast.dimension]?.[row]);
        if (k !== undefined) agg.add(mi * d + k, row);
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
    const restricted = new InnerAgg(ast.inner, c, n, aggCh(ast.inner), aggW(ast.inner));
    const unrestricted = new InnerAgg(ast.inner, c, n, aggCh(ast.inner), aggW(ast.inner));
    return {
      name,
      add(mi, row) {
        unrestricted.add(mi, row);
        if (c.dim[ast.whereChannel]?.[row] === ast.whereValue) restricted.add(mi, row);
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
  const agg = new InnerAgg(flat, c, n, aggCh(flat), aggW(flat));
  return {
    name,
    add(mi, row) {
      if (matches(c, flat.where, row)) agg.add(mi, row);
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

function passesContext(c: CompanionData, ctx: FoldContext, row: number): boolean {
  for (const ch in ctx.equals) if (c.dim[ch]?.[row] !== ctx.equals[ch]) return false;
  for (const ch in ctx.ranges) if (!inDateRange(c.temporal[ch]?.[row] ?? '', ctx.ranges[ch])) return false;
  return true;
}

/** Fold a tile's companion into per-mark measure values under the active context. Output arrays are
 *  indexed by mark index (via `markIndex`, the tile's id→index map); numeric measures come back as
 *  Float32Array, argmax/argmin as Uint16Array of codes into the dimension's canonical domain. A mark
 *  with no surviving fact is NaN / ARGMAX_UNKNOWN — the unknown colour. */
export function foldTile(
  c: CompanionData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  markIndex: Map<string, number>,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  const survived = new Uint8Array(markCount);
  const folders = measures.map((m) => makeFolder(m.name, m.ast, c, markCount, domains));

  for (let row = 0; row < c.rowCount; row++) {
    if (!passesContext(c, ctx, row)) continue;
    const mi = markIndex.get(c.mk[row]);
    if (mi === undefined) continue;
    survived[mi] = 1;
    for (const f of folders) f.add(mi, row);
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
