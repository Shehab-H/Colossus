import { hexToRgb, type RGB } from './colors';

// The scheme registry: named color ramps and palettes a view can reference by name (encoding.color.scheme).
// Three families, matched to the three kinds of color job (see colorScale.ts):
//   sequential  — magnitude (one perceptual/one-hue ramp, low→high)
//   diverging   — polarity  (two poles + neutral midpoint)
//   categorical — identity  (qualitative palette, assigned in fixed order)

// Sequential ramps. viridis/cividis are perceptually uniform + colorblind-safe; `blues` is the
// design-system single-hue ramp.
const SEQUENTIAL_HEX: Record<string, string[]> = {
  viridis: ['#440154', '#3b528b', '#21918c', '#5ec962', '#fde725'],
  plasma: ['#0d0887', '#7e03a8', '#cc4778', '#f89540', '#f0f921'],
  magma: ['#000004', '#51127c', '#b73779', '#fc8961', '#fcfdbf'],
  inferno: ['#000004', '#57106e', '#bc3754', '#f98e09', '#fcffa4'],
  cividis: ['#00204d', '#3d4c6b', '#7c7b78', '#bcaf6f', '#ffe945'],
  turbo: ['#30123b', '#3f9afd', '#1ae4b6', '#f9ba38', '#7a0403'],
  blues: ['#cde2fb', '#86b6ef', '#3987e5', '#1c5cab', '#0d366b'],
  greens: ['#e5f5e0', '#a1d99b', '#41ab5d', '#238b45', '#00441b'],
  oranges: ['#feedde', '#fdbe85', '#fd8d3c', '#e6550d', '#a63603'],
  greys: ['#1e1e1e', '#505050', '#828282', '#bebebe', '#f5f5f5'],
};

// Diverging ramps: cool pole → neutral gray → warm pole. `blueRed` is the design-system diverging pair.
const DIVERGING_HEX: Record<string, string[]> = {
  blueRed: ['#2a78d6', '#f0efec', '#e34948'],
  redBlue: ['#e34948', '#f0efec', '#2a78d6'],
  blueOrange: ['#2166ac', '#f7f7f7', '#b35806'],
  purpleGreen: ['#762a83', '#f7f7f7', '#1b7837'],
  spectral: ['#3288bd', '#99d594', '#ffffbf', '#fc8d59', '#d53e4f'],
};

// Categorical palettes, assigned in fixed order (never cycled for identity). `okabeIto` is validated
// all-pairs colorblind-safe (worst CVD ΔE ≈ 17) — the right default for map marks where any two
// categories can be adjacent. `category` is the design-system themed palette (adjacent-safe).
const CATEGORICAL_HEX: Record<string, string[]> = {
  okabeIto: ['#E69F00', '#56B4E9', '#009E73', '#F0E442', '#0072B2', '#D55E00', '#CC79A7', '#BBBBBB'],
  category: ['#3987e5', '#199e70', '#c98500', '#008300', '#9085e9', '#e66767', '#d55181', '#d95926'],
  status: ['#0ca30c', '#fab219', '#ec835a', '#d03b3b'],
};

export const DEFAULT_SEQUENTIAL = 'viridis';
export const DEFAULT_DIVERGING = 'blueRed';
export const DEFAULT_CATEGORICAL = 'okabeIto';

const toRgb = (hexes: string[]): RGB[] => hexes.map(hexToRgb);

export const sequentialStops = (name?: string): RGB[] => toRgb(SEQUENTIAL_HEX[name ?? ''] ?? SEQUENTIAL_HEX[DEFAULT_SEQUENTIAL]);
export const divergingStops = (name?: string): RGB[] => toRgb(DIVERGING_HEX[name ?? ''] ?? DIVERGING_HEX[DEFAULT_DIVERGING]);
export const categoricalColors = (name?: string): RGB[] => toRgb(CATEGORICAL_HEX[name ?? ''] ?? CATEGORICAL_HEX[DEFAULT_CATEGORICAL]);

/** All scheme names by family — for UI pickers and validation. */
export const schemeNames = () => ({
  sequential: Object.keys(SEQUENTIAL_HEX),
  diverging: Object.keys(DIVERGING_HEX),
  categorical: Object.keys(CATEGORICAL_HEX),
});
