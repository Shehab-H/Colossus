import type { Manifest } from './manifest';
import { filterableChannels, canonicalCategories, parseDateRange, ALL } from './channels';

// GPU filtering: every filter is a numeric range on one float slot of a DataFilterExtension. A filter
// change is then a uniform update (filterRange/filterEnabled layer props) — no fetch, decode, or
// re-upload. Slot values are built once per tile at decode; ranges are built per filter change on the
// main thread. This module is pure (no deck import) so all of it is unit-tested. See
// docs/gpu-residency/PHASE-1-gpu-filtering.md §1.

// Finite f32 sentinels (never Infinity — uniforms are f32). MAX_SAFE_F32 < f32 max (~3.4e38).
export const MAX_SAFE_F32 = 3.0e38;
// Null/NaN temporal marks: passes any `from` bound, fails any finite `to` (only an open `to`, whose
// sentinel is MAX_SAFE_F32 > NULL_DAY, keeps them) — reproduces isoDate('null') lexicographic order.
export const NULL_DAY = 2.0e38;
// A dict code for a tile value absent from the canonical category list: matched by nothing, kept by (all).
export const MISSING_CODE = 2.0e38;

const OPEN_RANGE: [number, number] = [-MAX_SAFE_F32, MAX_SAFE_F32];
// A selected dimension value not in the canonical list matches nothing (codes are ≥ 0).
const UNKNOWN_CATEGORY_RANGE: [number, number] = [-2, -2];

export interface SlotSpec {
  name: string;
  kind: 'temporal' | 'dimension';
  categories?: string[]; // dimension slots: the canonical category order; codes index this array
}

export interface FilterSlots {
  specs: SlotSpec[];
  size: 1 | 2 | 3 | 4;
}

/** The filter slots for a view: one per filterable (dimension/temporal) channel, in a deterministic
 *  order, capped at 4 (DataFilterExtension's max). Dimension slots carry the canonical category order
 *  (baked domain, else the discovered option list — same array the UI shows, so codes and options can
 *  never disagree). Null when the view has no filterable channels. */
export function filterSlots(manifest: Manifest, options?: Record<string, string[]>): FilterSlots | null {
  const channels = filterableChannels(manifest.view);
  if (channels.length === 0) return null;
  if (channels.length > 4)
    console.warn(
      `view "${manifest.view.id}" has ${channels.length} filterable channels; GPU filtering supports 4 — ` +
        `channels past the 4th are not filtered.`,
    );
  const specs: SlotSpec[] = channels.slice(0, 4).map((ch) => {
    const temporal = ch.role === 'temporal' || ch.type === 'date';
    if (temporal) return { name: ch.name, kind: 'temporal' as const };
    return { name: ch.name, kind: 'dimension' as const, categories: canonicalCategories(manifest, ch.name) ?? options?.[ch.name] };
  });
  return { specs, size: specs.length as 1 | 2 | 3 | 4 };
}

/** Day number (days since Unix epoch, floored) for a raw temporal value — mirrors tileData's isoDate
 *  storage heuristic exactly, so a filter range built from endpoint strings compares equal to the
 *  column's own day numbers. null/NaN → NULL_DAY. */
export function dayNumber(v: unknown): number {
  if (v === null || v === undefined) return NULL_DAY;
  if (v instanceof Date) return Math.floor(v.getTime() / 86400000);
  const n = Number(v);
  if (Number.isFinite(n)) {
    const ms = Math.abs(n) < 1e7 ? n * 86400000 : n; // day-count vs epoch-millis storage
    return Math.floor(ms / 86400000);
  }
  if (typeof v === 'string') return dayNumberOfIso(v); // a date string like YYYY-MM-DD
  return NULL_DAY;
}

/** Day number for a filter endpoint 'YYYY-MM-DD'. Unparseable → NULL_DAY (treated as no real value). */
export function dayNumberOfIso(s: string): number {
  const [y, m, d] = s.slice(0, 10).split('-').map(Number);
  if (!Number.isFinite(y) || !Number.isFinite(m) || !Number.isFinite(d)) return NULL_DAY;
  return Math.floor(Date.UTC(y, m - 1, d) / 86400000);
}

/** The per-slot filter ranges for the active selection, in slot order. An absent/`(all)` selection or a
 *  temporal value with no real bounds is wide-open; a dimension equality is [code, code] (unknown value
 *  → [-2,-2]); an open-ended date side uses the ± sentinel. */
export function filterRanges(slots: FilterSlots, filters: Record<string, string>): [number, number][] {
  return slots.specs.map((spec) => {
    const value = filters[spec.name];
    if (!value || value === ALL) return OPEN_RANGE;
    if (spec.kind === 'temporal') {
      const r = parseDateRange(value);
      if (!r) return OPEN_RANGE;
      return [r.from ? dayNumberOfIso(r.from) : -MAX_SAFE_F32, r.to ? dayNumberOfIso(r.to) : MAX_SAFE_F32];
    }
    const code = spec.categories?.indexOf(value) ?? -1;
    return code < 0 ? UNKNOWN_CATEGORY_RANGE : [code, code];
  });
}

/** Whether any slot is an active predicate (not wide-open) — drives filterEnabled (skip shader cost on
 *  an unfiltered view without changing the layer's prop shape). */
export function anyActive(ranges: [number, number][]): boolean {
  return ranges.some(([lo, hi]) => lo !== OPEN_RANGE[0] || hi !== OPEN_RANGE[1]);
}

/** Local dict codes → canonical codes for one dimension slot: lut[localCode] = index of that string in
 *  the canonical categories, or MISSING_CODE when absent (legacy manifests) / categories unknown. */
export function canonicalCodeLut(dict: string[], categories: string[] | undefined): Float32Array {
  const lut = new Float32Array(dict.length);
  if (!categories) {
    lut.fill(MISSING_CODE);
    return lut;
  }
  const codeOf = new Map<string, number>();
  for (let i = 0; i < categories.length; i++) codeOf.set(categories[i], i);
  for (let i = 0; i < dict.length; i++) {
    const c = codeOf.get(dict[i]);
    lut[i] = c === undefined ? MISSING_CODE : c;
  }
  return lut;
}

/** Interleave per-mark slot values into deck's binary getFilterValue attribute. Points are per-mark
 *  (`count * size`); polygons expand per-vertex via polyStartIndices (`vertexCount * size`) — the value
 *  of mark p repeated across its ring's vertices, exactly the color expansion tileDeckData uses. */
export function buildFilterValues(
  size: number,
  count: number,
  perMarkSlots: Float32Array[],
  polyStartIndices?: Uint32Array,
  vertexCount?: number,
): Float32Array {
  if (polyStartIndices && vertexCount !== undefined) {
    const out = new Float32Array(vertexCount * size);
    for (let p = 0; p < count; p++) {
      for (let v = polyStartIndices[p]; v < polyStartIndices[p + 1]; v++) {
        const base = v * size;
        for (let s = 0; s < size; s++) out[base + s] = perMarkSlots[s][p];
      }
    }
    return out;
  }
  const out = new Float32Array(count * size);
  for (let i = 0; i < count; i++) {
    const base = i * size;
    for (let s = 0; s < size; s++) out[base + s] = perMarkSlots[s][i];
  }
  return out;
}
