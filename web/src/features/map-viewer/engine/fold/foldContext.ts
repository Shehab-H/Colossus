import { type DateRange, parseDateRange, parseNumericRange } from '../filters/filterValue';
import type { ViewConfig, ChannelSpec } from '@/types/viewConfig';

/** The active fold context: perFact equality selections + ordered temporal/numeric ranges. */
export interface FoldContext {
  equals: Record<string, string>;
  ranges: Record<string, DateRange>;
}

/** Build the fold context from active perFact selections: dateRange/range controls become ordered ranges;
 *  the rest are equality selections. */
export function buildFoldContext(view: ViewConfig, context: Record<string, string>): FoldContext {
  const byName = new Map<string, ChannelSpec>(view.source.channels.map((ch) => [ch.name, ch]));
  const controls = new Map(view.filters?.map((filter) => [filter.channel, filter.control] as const) ?? []);
  const equals: Record<string, string> = {};
  const ranges: Record<string, DateRange> = {};
  for (const [name, v] of Object.entries(context)) {
    const ch = byName.get(name);
    if (!ch) continue;
    if (ch.role === 'temporal' || ch.type === 'date') {
      const r = parseDateRange(v);
      if (r) ranges[name] = r;
    } else if (controls.get(name) === 'range') {
      const r = parseNumericRange(v);
      if (r) ranges[name] = { from: r.from, to: r.to };
    } else {
      equals[name] = v;
    }
  }
  return { equals, ranges };
}
