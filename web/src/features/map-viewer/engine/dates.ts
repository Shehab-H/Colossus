// Filter-value helpers shared by the channel model and the measure fold. Kept dependency-free so both
// channels.ts and measures.ts can import them without a cycle.

/** Sentinel filter value meaning "no predicate on this channel". */
export const ALL = '(all)';

/** Temporal channels filter as a range: the selection string is `from..to`, either side optionally
 *  empty for an open bound. A bare date (no separator) is read as that single day, so legacy single-date
 *  selections and embed URLs keep working. */
export const RANGE_SEP = '..';

export interface DateRange {
  from: string;
  to: string;
}

/** Parse a temporal filter value into its (inclusive, possibly open) bounds, or null for "no predicate". */
export function parseDateRange(value: string | undefined): DateRange | null {
  if (!value || value === ALL) return null;
  const i = value.indexOf(RANGE_SEP);
  if (i < 0) return { from: value, to: value }; // legacy single-day selection
  const from = value.slice(0, i);
  const to = value.slice(i + RANGE_SEP.length);
  return from || to ? { from, to } : null;
}

/** Encode a from/to pair to a filter value; empty on both sides collapses to ALL (no predicate). */
export function makeDateRange(from: string, to: string): string {
  return from || to ? `${from}${RANGE_SEP}${to}` : ALL;
}

/** Whether a normalized YYYY-MM-DD value falls within a range. Lexicographic comparison of that fixed
 *  form equals chronological order, so no date parsing is needed on the per-row hot path. */
export function inDateRange(s: string, r: DateRange): boolean {
  return (!r.from || s >= r.from) && (!r.to || s <= r.to);
}

// Finite f32 sentinels (never Infinity — GPU uniforms are f32). MAX_SAFE_F32 < f32 max (~3.4e38).
export const MAX_SAFE_F32 = 3.0e38;
// Null/NaN temporal marks: passes any `from` bound, fails any finite `to` (only an open `to`, whose
// sentinel is MAX_SAFE_F32 > NULL_DAY, keeps them) — reproduces isoDate('null') lexicographic order.
export const NULL_DAY = 2.0e38;

/** Day number (days since Unix epoch, floored) for a raw temporal value — mirrors tileData's isoDate
 *  storage heuristic exactly, so a range built from endpoint strings compares equal to a column's own
 *  day numbers. null/NaN → NULL_DAY. */
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
