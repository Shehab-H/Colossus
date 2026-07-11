// Client mirror of the measure grammar (VIEW_CONFIG §4 / Colossus.Domain.Measures). Same AST, same
// errors, verified against the shared fixture tests/fixtures/measure-cases.json. The fold engine
// (foldTile, below) recomputes each measure over a tile's fact partials under the active context.

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
