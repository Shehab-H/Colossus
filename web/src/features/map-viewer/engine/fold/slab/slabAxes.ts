import type { SlabAxis } from '@/types/manifest';

/** Cell-order strides (fastest/last axis = stride 1). */
export function computeStrides(axes: SlabAxis[]): number[] {
  const strides = new Array<number>(axes.length);
  let s = 1;
  for (let i = axes.length - 1; i >= 0; i--) {
    strides[i] = s;
    s *= axes[i].cardinality;
  }
  return strides;
}

// ── fold ────────────────────────────────────────────────────────────────────────────────────────────

export const axisIndex = (axes: SlabAxis[], channel: string): number => axes.findIndex((a) => a.name === channel);

/** Resolve an ordered range to [loBin, hiBin] over the axis's sorted domain. ISO dates compare
 *  lexically; numeric axes compare parsed numbers so, for example, 10 correctly follows 2. */
export function resolveBins(axis: SlabAxis, from: string, to: string): [number, number] {
  const domain = axis.domain;
  let lo = 0;
  let hi = domain.length - 1;
  if (axis.valueType === 'number') {
    const lower = from ? Number(from) : undefined;
    const upper = to ? Number(to) : undefined;
    if (lower !== undefined) while (lo < domain.length && Number(domain[lo]) < lower) lo++;
    if (upper !== undefined) while (hi >= 0 && Number(domain[hi]) > upper) hi--;
  } else {
    if (from) while (lo < domain.length && domain[lo] < from) lo++;
    if (to) while (hi >= 0 && domain[hi] > to) hi--;
  }
  return [lo, hi];
}
