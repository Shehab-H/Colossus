import type { ChannelSpec, ViewConfig } from './manifest';
import { tileUrl } from './manifest';
import { fetchArrowTable } from './arrow';
import type { ColorDomain } from './colorScale';

/** Sentinel filter value meaning "no predicate on this channel". */
export const ALL = '(all)';

/** Channel types that carry a real number line (so continuous/binned color scales apply). Everything
 *  else (dict, date) is treated as discrete categories for coloring. */
export const NUMERIC_TYPES = new Set(['f32', 'f64', 'u8', 'u16', 'i32', 'i64']);

export const isNumericChannel = (ch: ChannelSpec | undefined): boolean => !!ch && NUMERIC_TYPES.has(ch.type);

/** Every measure channel — carried in every tile, and the usual choice to color by. */
export const measureChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'measure');

/** Any channel a view can color by (every carried channel). */
export const colorableChannels = (view: ViewConfig): ChannelSpec[] => view.source.channels;

export const filterableChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'dimension' || c.role === 'temporal');

/** The channel a view colors by initially: the authored `encoding.color.channel` if it's real, else the
 *  first measure, else the first channel. */
export const colorChannelName = (view: ViewConfig): string => {
  const authored = view.encoding?.color?.channel;
  if (authored && view.source.channels.some((c) => c.name === authored)) return authored;
  return measureChannels(view)[0]?.name ?? view.source.channels[0]?.name ?? 'value';
};

// A channel as a comparable/displayable string. Temporal values are normalized to YYYY-MM-DD whether
// the tile stored a real DATE or day-integers, so labels, filters, and <input type=date> all agree.
export const channelSqlExpr = (ch: ChannelSpec): string =>
  ch.role === 'temporal'
    ? `strftime(DATE '1970-01-01' + CAST("${ch.name}" AS INTEGER), '%Y-%m-%d')`
    : `CAST("${ch.name}" AS VARCHAR)`;

/** WHERE clause from the active filter selections, using each channel's normalized expression. */
export function buildWhere(view: ViewConfig, filters: Record<string, string>): string {
  const byName = new Map(view.source.channels.map((c) => [c.name, c] as const));
  return Object.entries(filters)
    .filter(([, v]) => v && v !== ALL)
    .map(([name, v]) => {
      const ch = byName.get(name);
      const lhs = ch ? channelSqlExpr(ch) : `CAST("${name}" AS VARCHAR)`;
      return `${lhs} = '${v.replace(/'/g, "''")}'`;
    })
    .join(' AND ');
}

/** Describe a color channel's domain from the root tile (no full-store scan): a numeric [min,max] plus a
 *  value sample (for quantile breaks), or the sorted distinct categories. Drives which scale can apply
 *  and how it's placed. Per-channel so switching what you color by rescales correctly. */
export async function describeColorDomain(view: ViewConfig, version: string, channel: string): Promise<ColorDomain> {
  const spec = view.source.channels.find((c) => c.name === channel);
  const numeric = isNumericChannel(spec);
  const t = await fetchArrowTable(tileUrl(view.id, version, 0, 0, 0));
  const col = t.getChild(channel);
  if (!col) return numeric ? { kind: 'numeric', min: 0, max: 1 } : { kind: 'categorical', categories: [] };

  if (numeric) {
    const a = col.toArray() as ArrayLike<number>;
    let min = Infinity;
    let max = -Infinity;
    const sample: number[] = [];
    for (let i = 0; i < a.length; i++) {
      const v = a[i];
      if (v < min) min = v;
      if (v > max) max = v;
      sample.push(v);
    }
    if (!Number.isFinite(min)) return { kind: 'numeric', min: 0, max: 1 };
    return { kind: 'numeric', min, max, sample };
  }

  const set = new Set<string>();
  for (let i = 0; i < t.numRows; i++) set.add(String(col.get(i)));
  return { kind: 'categorical', categories: [...set].sort() };
}

/** Distinct values of each filterable (dimension/temporal) channel, for the filter controls. Read from
 *  the root tile's Arrow buffer. */
export async function discoverOptions(view: ViewConfig, version: string): Promise<Record<string, string[]>> {
  const channels = filterableChannels(view);
  if (channels.length === 0) return {};
  const t = await fetchArrowTable(tileUrl(view.id, version, 0, 0, 0));
  const options: Record<string, string[]> = {};
  for (const ch of channels) {
    const col = t.getChild(ch.name);
    const set = new Set<string>();
    if (col) for (let i = 0; i < t.numRows; i++) set.add(String(col.get(i)));
    options[ch.name] = [...set].sort();
  }
  return options;
}
