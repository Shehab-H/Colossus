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
