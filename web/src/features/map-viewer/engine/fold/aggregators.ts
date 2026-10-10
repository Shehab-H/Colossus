import type { CompanionData } from './companion';
import type { Agg } from './measureGrammar';

/** Argmax code for a mark with no surviving facts — past any real domain, so the colour LUT paints it
 *  the unknown colour, exactly like the empty-set numeric NaN. */
export const ARGMAX_UNKNOWN = 0xffff;

type Numeric = { kind: 'sum' | 'count' | 'avg' | 'wavg' | 'min' | 'max' };

/** Accumulates one numeric inner-agg over a set of groups (a group is a mark, or a (mark, dim) pair for
 *  argmax, or restricted/unrestricted halves for share). Additive partials in, finalized value out.
 *  Exported so the slab fold (fold/slab) reuses the identical accumulation + finalization — the fold math
 *  is frozen (byte-for-byte), only the traversal differs. The constructor reads only `c.partial`/
 *  `c.rowCount`, so the slab passes its planes as a minimal companion. */
export class InnerAgg {
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
/** A dense inner-agg over `groups`, fed range-folded (g,m) contributions. Value/finalization mirror
 *  aggregators.ts InnerAgg (frozen) — the fixture pins it byte-identical. */
export class DenseAgg {
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

// ── shared finalization (identical for sparse InnerAgg and dense DenseAgg) ──────────────────────────────

interface Valued {
  has(g: number): boolean;
  value(g: number): number;
}

export function plainFinalize(agg: Valued, survived: Uint8Array, n: number): Float32Array {
  const out = new Float32Array(n);
  for (let m = 0; m < n; m++) out[m] = survived[m] ? agg.value(m) : NaN;
  return out;
}

export function shareFinalize(restricted: Valued, unrestricted: Valued, survived: Uint8Array, n: number): Float32Array {
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

export function argmaxFinalize(agg: Valued, survived: Uint8Array, n: number, d: number, isMax: boolean): Uint16Array {
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
