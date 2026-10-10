import type { RGB } from './palette';
import type { ScaleType, ColorSpec } from '@/types/viewConfig';
import { formatValue } from './valueFormat';
import { resolveScale, type ColorDomain } from './scale';

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
