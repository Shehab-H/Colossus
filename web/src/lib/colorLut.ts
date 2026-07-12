import type { ColorSpec } from './manifest';
import { buildColorScale, scaleShape, type ColorDomain } from './colorScale';

// The color scale, sampled into a small RGBA8 texture the GPU reads per mark. `buildColorScale` stays
// the single authority: every texel is `colorOf` evaluated at a real value, so the GPU can't disagree
// with the CPU legend. A recolor swaps this texture + a few uniforms — no per-mark data is touched.

const NUMERIC_WIDTH = 1024; // band-edge error ≤ domainSpan/1024, accepted tolerance (see PHASE-2 §1.1)
const LOG_MIN = 1e-9; // mirrors colorScale.normalize('log') clamping

export interface ColorLut {
  texels: Uint8Array; // RGBA8, width * 4
  width: number;
  /** numeric: the value→t domain the shader normalizes against (log space when `transform === 1`). */
  domain: [number, number];
  /** 0 linear (value space), 1 log space. sqrt/diverging/binned bake into a value-space LUT (0). */
  transform: 0 | 1;
  kind: 'numeric' | 'categorical';
  /** categorical only: the canonical order the per-mark codes index into (last texel is `unknown`). */
  categories?: string[];
  /** null/out-of-domain color, RGB 0–255 (shader gets it as a uniform for the NaN / missing path). */
  unknown: [number, number, number];
}

/** Build the GPU lookup table for a color encoding. Numeric domains sample `colorOf` across the ramp
 *  (log-spaced for log scales); categorical domains hold one texel per category plus a trailing
 *  `unknown` texel for out-of-domain codes. */
export function buildColorLut(spec: ColorSpec, domain: ColorDomain): ColorLut {
  const colorOf = buildColorScale(spec, domain);
  const unknown = colorOf(null); // == the scale's `unknown` (spec.unknown or the gray fallback)
  const shape = scaleShape(spec, domain);

  if (shape.kind === 'categorical') {
    const cats = shape.categories;
    const width = cats.length + 1; // +1 trailing texel for the unknown / out-of-domain color
    const texels = new Uint8Array(width * 4);
    for (let c = 0; c < cats.length; c++) writeTexel(texels, c, colorOf(cats[c]));
    writeTexel(texels, cats.length, unknown);
    return { texels, width, domain: [0, 0], transform: 0, kind: 'categorical', categories: cats, unknown };
  }

  const log = shape.log;
  const lo = log ? Math.max(shape.lo, LOG_MIN) : shape.lo;
  const hi = log ? Math.max(shape.hi, lo * (1 + 1e-9)) : shape.hi;
  const dLo = log ? Math.log(lo) : lo;
  const dHi = log ? Math.log(hi) : hi;

  const texels = new Uint8Array(NUMERIC_WIDTH * 4);
  for (let i = 0; i < NUMERIC_WIDTH; i++) {
    const t = i / (NUMERIC_WIDTH - 1);
    const value = log ? Math.exp(dLo + t * (dHi - dLo)) : dLo + t * (dHi - dLo);
    writeTexel(texels, i, colorOf(value));
  }
  return { texels, width: NUMERIC_WIDTH, domain: [dLo, dHi], transform: log ? 1 : 0, kind: 'numeric', unknown };
}

function writeTexel(texels: Uint8Array, i: number, [r, g, b]: [number, number, number]): void {
  const o = i * 4;
  texels[o] = r;
  texels[o + 1] = g;
  texels[o + 2] = b;
  texels[o + 3] = 255;
}
