import { hexToRgb, interpolate, type RGB } from './colors';
import { categoricalColors, divergingStops, sequentialStops } from './schemes';
import type { ColorSpec, ScaleType } from './manifest';

/** What the data actually looks like for the color channel, derived from the tiles (see
 *  channels.describeColorDomain). A scale needs this to place values on its ramp / assign categories. */
export type ColorDomain =
  | { kind: 'numeric'; min: number; max: number; sample?: number[] }
  | { kind: 'categorical'; categories: string[] };

/** A built color scale: value (of any carried datatype) → RGB. */
export type ColorFn = (value: number | string | null | undefined) => RGB;

const FALLBACK: RGB = [136, 136, 136];

/** A stable category key for ANY datatype — string, number, boolean, or date-as-integer — so explicit
 *  palettes and auto-assignment work beyond strings. null/undefined map to a distinct sentinel. */
export function categoryKey(v: number | string | null | undefined): string {
  return v === null || v === undefined ? ' null' : String(v);
}

/** Resolve the scale type: explicit `type` wins, else inferred from the spec's shape and the data. */
export function inferType(spec: ColorSpec, domain: ColorDomain): ScaleType {
  if (spec.type) return spec.type;
  if (spec.palette) return 'categorical';
  if (spec.thresholds) return 'threshold';
  if (domain.kind === 'categorical') return 'categorical';
  return 'linear';
}

// The scale resolved to concrete structure — the single source both the color function and the legend
// derive from, so a swatch can never disagree with a rendered mark.
type Resolved =
  | { kind: 'continuous'; type: ScaleType; diverging: boolean; stops: RGB[]; min: number; max: number; midpoint: number; minClamped: boolean; maxClamped: boolean; unknown: RGB }
  | { kind: 'binned'; type: ScaleType; colors: RGB[]; breaks: number[]; unknown: RGB }
  | { kind: 'categorical'; type: ScaleType; map: Map<string, RGB>; order: string[]; unknown: RGB; closed: boolean };

const toNumber = (v: number | string | null | undefined): number | null => {
  if (v === null || v === undefined) return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
};

// Position a value in [0,1] under a continuous transform.
function normalize(type: ScaleType, v: number, min: number, max: number): number {
  if (type === 'log') {
    const lo = Math.max(min, 1e-9);
    const hi = Math.max(max, lo * (1 + 1e-9));
    return (Math.log(Math.max(v, lo)) - Math.log(lo)) / (Math.log(hi) - Math.log(lo));
  }
  if (type === 'sqrt') {
    const span = Math.sqrt(Math.max(0, max - min)) || 1;
    return Math.sqrt(Math.max(0, v - min)) / span;
  }
  return (v - min) / ((max - min) || 1);
}

// Two-arm position for a diverging scale: min→0, midpoint→0.5, max→1.
function divergingT(n: number, min: number, max: number, mid: number): number {
  return n <= mid
    ? (mid === min ? 0 : 0.5 * ((n - min) / (mid - min)))
    : (max === mid ? 1 : 0.5 + 0.5 * ((n - mid) / (max - mid)));
}

/** Robust default bounds for a continuous ramp: raw min/max lets a single outlier compress nearly every
 *  mark into one end of the ramp, so without an authored domain the ramp spans p02..p98 of the sample
 *  and out-of-range values clamp to the ends (interpolate clamps t). */
function robustBounds(sample: number[], min: number, max: number): [number, number] {
  let s = sample.filter(Number.isFinite);
  if (s.length < 8) return [min, max];
  if (s.length > 10_000) {
    const stride = Math.ceil(s.length / 10_000);
    const sub: number[] = [];
    for (let i = 0; i < s.length; i += stride) sub.push(s[i]);
    s = sub;
  }
  s.sort((a, b) => a - b);
  const at = (q: number) => {
    const p = q * (s.length - 1);
    const lo = Math.floor(p);
    const hi = Math.ceil(p);
    return s[lo] + (s[hi] - s[lo]) * (p - lo);
  };
  const lo = at(0.02);
  const hi = at(0.98);
  return lo < hi ? [lo, hi] : [min, max];
}

function quantileBreaks(sample: number[], bins: number): number[] {
  const s = sample.filter(Number.isFinite).slice().sort((a, b) => a - b);
  if (s.length === 0 || bins <= 1) return [];
  const breaks: number[] = [];
  for (let i = 1; i < bins; i++) {
    const q = (i / bins) * (s.length - 1);
    const lo = Math.floor(q);
    const hi = Math.ceil(q);
    breaks.push(s[lo] + (s[hi] - s[lo]) * (q - lo));
  }
  return breaks;
}

const uniq = (xs: string[]): string[] => {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const x of xs) if (!seen.has(x)) { seen.add(x); out.push(x); }
  return out;
};

function resolveScale(spec: ColorSpec, domain: ColorDomain): Resolved {
  const type = inferType(spec, domain);
  const unknown: RGB = spec.unknown ? hexToRgb(spec.unknown) : FALLBACK;
  const explicitRange = spec.range?.map(hexToRgb);

  if (type === 'categorical' || type === 'ordinal') {
    const authored = spec.domain?.map(String);
    const categories = authored ?? (domain.kind === 'categorical' ? domain.categories : []);
    const map = new Map<string, RGB>();
    if (spec.palette) for (const [k, hex] of Object.entries(spec.palette)) map.set(k, hexToRgb(hex));

    if (type === 'ordinal') {
      // Ordered categories sample the ramp in domain order; an explicit palette entry overrides a step.
      const stops = explicitRange ?? sequentialStops(spec.scheme);
      const n = Math.max(1, categories.length - 1);
      categories.forEach((c, i) => { if (!map.has(c)) map.set(c, interpolate(stops, i / n)); });
    } else if (!spec.palette) {
      // Pure nominal: auto-assign every category from the scheme in fixed order (wraps past its length).
      // With an explicit palette present, that map is the closed set — unmapped values fall to `unknown`.
      const palette = explicitRange ?? categoricalColors(spec.scheme);
      categories.forEach((c, i) => { if (!map.has(c)) map.set(c, palette[i % palette.length]); });
    }
    const order = uniq(spec.palette ? [...Object.keys(spec.palette), ...categories] : categories);
    return { kind: 'categorical', type, map, order, unknown, closed: !!spec.palette };
  }

  // Continuous input → numeric domain (honor an authored [min,max] override, else robust bounds).
  const dmin = domain.kind === 'numeric' ? domain.min : 0;
  const dmax = domain.kind === 'numeric' ? domain.max : 1;
  const robust = domain.kind === 'numeric' && domain.sample?.length ? robustBounds(domain.sample, dmin, dmax) : ([dmin, dmax] as [number, number]);
  const min = typeof spec.domain?.[0] === 'number' ? (spec.domain[0] as number) : robust[0];
  const max = typeof spec.domain?.[1] === 'number' ? (spec.domain[1] as number) : robust[1];

  const base = explicitRange ?? (type === 'diverging' ? divergingStops(spec.scheme) : sequentialStops(spec.scheme));
  const stops = spec.reverse ? [...base].reverse() : base;

  if (type === 'quantize' || type === 'quantile' || type === 'threshold') {
    let breaks: number[];
    if (type === 'threshold') breaks = (spec.thresholds ?? []).slice().sort((a, b) => a - b);
    else if (type === 'quantile') breaks = quantileBreaks(domain.kind === 'numeric' ? domain.sample ?? [] : [], spec.bins ?? 5);
    else {
      const b = Math.max(1, spec.bins ?? 5);
      breaks = [];
      for (let i = 1; i < b; i++) breaks.push(min + ((max - min) * i) / b);
    }
    const buckets = breaks.length + 1;
    const colors: RGB[] = [];
    for (let i = 0; i < buckets; i++) colors.push(interpolate(stops, buckets === 1 ? 0.5 : i / (buckets - 1)));
    return { kind: 'binned', type, colors, breaks, unknown };
  }

  const diverging = type === 'diverging';
  return {
    kind: 'continuous', type, diverging, stops, min, max,
    midpoint: spec.midpoint ?? (min + max) / 2,
    minClamped: min > dmin, maxClamped: max < dmax,
    unknown,
  };
}

/** Build a color function from an encoding spec + the observed domain. Covers every scale type:
 *  continuous (linear/log/sqrt), diverging, binned (quantize/quantile/threshold), and discrete
 *  (categorical/ordinal). Unmapped or null values get `unknown` (default gray). */
export function buildColorScale(spec: ColorSpec, domain: ColorDomain): ColorFn {
  const r = resolveScale(spec, domain);
  if (r.kind === 'categorical') return (v) => r.map.get(categoryKey(v)) ?? r.unknown;
  if (r.kind === 'binned') {
    return (v) => {
      const n = toNumber(v);
      if (n === null) return r.unknown;
      let i = 0;
      while (i < r.breaks.length && n >= r.breaks[i]) i++;
      return r.colors[i];
    };
  }
  return (v) => {
    const n = toNumber(v);
    if (n === null) return r.unknown;
    const t = r.diverging ? divergingT(n, r.min, r.max, r.midpoint) : normalize(r.type, n, r.min, r.max);
    return interpolate(r.stops, t);
  };
}

// ── Legend ──────────────────────────────────────────────────────────────────────────────────────
// A structured description of what the coloring means, derived from the SAME resolved scale so it can
// never drift from the rendered marks. Every map with a color-by shows one (components/Legend.tsx).

export interface LegendItem {
  color: RGB;
  label: string;
}

export interface Legend {
  channel: string;
  /** Human name of the scale nature: "gradient", "diverging", "binned", "categorical", … */
  note: string;
  kind: 'continuous' | 'binned' | 'categorical';
  /** continuous: ramp stops for a gradient bar + the axis labels. */
  gradient?: RGB[];
  min?: number;
  max?: number;
  midpoint?: number;
  /** Values exist beyond this end of the ramp and clamp to its color (robust/authored domain). */
  minClamped?: boolean;
  maxClamped?: boolean;
  /** binned / categorical: labelled swatches. */
  items?: LegendItem[];
  /** categorical: how many categories are hidden past the display cap. */
  more?: number;
}

const NOTE: Record<ScaleType, string> = {
  linear: 'gradient',
  log: 'log scale',
  sqrt: 'square-root scale',
  diverging: 'diverging',
  quantize: 'binned',
  quantile: 'quantiles',
  threshold: 'thresholds',
  ordinal: 'ordinal',
  categorical: 'categorical',
};

const CAP = 10;

/** Compact number formatting for legend labels. */
export function formatValue(n: number): string {
  if (!Number.isFinite(n)) return String(n);
  const a = Math.abs(n);
  if (a >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (a >= 1000) return `${(n / 1000).toFixed(1)}k`;
  if (Number.isInteger(n)) return String(n);
  return n.toFixed(a < 10 ? 1 : 0);
}

function bucketLabel(i: number, breaks: number[]): string {
  if (breaks.length === 0) return 'all';
  if (i === 0) return `< ${formatValue(breaks[0])}`;
  if (i === breaks.length) return `≥ ${formatValue(breaks[breaks.length - 1])}`;
  return `${formatValue(breaks[i - 1])} – ${formatValue(breaks[i])}`;
}

/** Describe the coloring for a legend, or null when there's nothing meaningful to show yet. */
export function describeLegend(spec: ColorSpec, domain: ColorDomain, channel: string): Legend | null {
  const r = resolveScale(spec, domain);
  const note = NOTE[r.type];

  if (r.kind === 'continuous') {
    return {
      channel, note, kind: 'continuous', gradient: r.stops, min: r.min, max: r.max,
      midpoint: r.diverging ? r.midpoint : undefined,
      minClamped: r.minClamped || undefined, maxClamped: r.maxClamped || undefined,
    };
  }
  if (r.kind === 'binned') {
    return { channel, note, kind: 'binned', items: r.colors.map((color, i) => ({ color, label: bucketLabel(i, r.breaks) })) };
  }
  if (r.order.length === 0) return null; // categorical with no data yet
  const items = r.order.slice(0, CAP).map((c) => ({ color: r.map.get(c) ?? r.unknown, label: c }));
  if (r.closed) items.push({ color: r.unknown, label: 'other' });
  return { channel, note, kind: 'categorical', items, more: r.order.length > CAP ? r.order.length - CAP : undefined };
}
