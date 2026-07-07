// Value → RGB ramp (viridis-ish, 5 stops). Applied once per tile at load time into a Uint8Array,
// so color lives in a binary attribute — no per-point JS objects reach the render loop.

const STOPS: [number, number, number][] = [
  [68, 1, 84],
  [59, 82, 139],
  [33, 145, 140],
  [94, 201, 98],
  [253, 231, 37],
];

function rampColor(t: number): [number, number, number] {
  const c = Math.max(0, Math.min(1, t)) * (STOPS.length - 1);
  const i = Math.floor(c);
  const f = c - i;
  const a = STOPS[i];
  const b = STOPS[Math.min(i + 1, STOPS.length - 1)];
  return [
    Math.round(a[0] + (b[0] - a[0]) * f),
    Math.round(a[1] + (b[1] - a[1]) * f),
    Math.round(a[2] + (b[2] - a[2]) * f),
  ];
}

/** Builds a packed RGB buffer (size 3·n) from a value column, normalizing over [min, max]. */
export function valuesToColors(values: ArrayLike<number>, min = 0, max = 100): Uint8Array {
  const n = values.length;
  const out = new Uint8Array(n * 3);
  const span = max - min || 1;
  for (let i = 0; i < n; i++) {
    const [r, g, b] = rampColor((values[i] - min) / span);
    out[i * 3] = r;
    out[i * 3 + 1] = g;
    out[i * 3 + 2] = b;
  }
  return out;
}
