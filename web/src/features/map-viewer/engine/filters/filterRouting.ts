import type { ViewConfig, ChannelSpec } from '@/types/viewConfig';
import { defaultFilterControl, filterControl } from './filterControls';
import type { Manifest } from '@/types/manifest';
import { isGroupRegime, isNumericChannel } from '../view/channelRoles';
import { ALL } from './filterValue';

export const filterableChannels = (view: ViewConfig): ChannelSpec[] => {
  if (view.filters === undefined) return view.source.channels.filter((channel) => defaultFilterControl(channel) !== null);
  const byName = new Map(view.source.channels.map((channel) => [channel.name, channel]));
  return view.filters.map((filter) => byName.get(filter.channel)).filter((channel): channel is ChannelSpec => channel !== undefined);
};

/** The filterable channels a view can actually answer, so only live controls render. Group regime:
 *  all of them (perMark → GPU predicate, perFact → fold context). Row aggregate tiles drop categorical
 *  and temporal source columns, but retain reducer-produced numeric measures; an explicitly configured
 *  range can therefore predicate the rendered value while unavailable controls stay hidden. */
export function carriedFilterableChannels(manifest: Manifest): ChannelSpec[] {
  if (isGroupRegime(manifest.view)) return filterableChannels(manifest.view);
  const channels = filterableChannels(manifest.view);
  if (manifest.reduction !== 'aggregate') return channels;
  return channels.filter((channel) =>
    filterControl(manifest.view, channel) === 'range' && channel.role === 'measure' && isNumericChannel(channel));
}

/** The channels whose values ride in the render tiles for the GPU filter. Group regime: the perMark
 *  ones only — a perFact selection is fold context and must never become a filterRange (no mark may be
 *  discarded for it). */
export function predicateChannels(manifest: Manifest): ChannelSpec[] {
  const carried = carriedFilterableChannels(manifest);
  if (!isGroupRegime(manifest.view)) return carried;
  const perFact = new Set(manifest.factChannels?.perFact ?? []);
  return carried.filter((c) => !perFact.has(c.name));
}

/** Split the active filters by classification: perFact filters are fold *context* (they never reach tile
 *  decode); everything else is a GPU *predicate* exactly as in the row regime. Row-regime views (no
 *  `factChannels`) yield an all-predicate split — unchanged behaviour. */
export interface SplitFilters {
  predicate: Record<string, string>;
  context: Record<string, string>;
}

export function splitFilters(manifest: Manifest, active: Record<string, string>): SplitFilters {
  const perFact = new Set(manifest.factChannels?.perFact ?? []);
  const predicate: Record<string, string> = {};
  const context: Record<string, string> = {};
  for (const [name, v] of Object.entries(active)) (perFact.has(name) ? context : predicate)[name] = v;
  return { predicate, context };
}

/** The active (non-ALL) filter selections, restricted to channels the view can filter — a stale
 *  selection left over from a previous view can never leak into another view's predicate. */
export function activeFilters(view: ViewConfig, filters: Record<string, string>): Record<string, string> {
  const names = new Set(filterableChannels(view).map((c) => c.name));
  const out: Record<string, string> = {};
  for (const [name, v] of Object.entries(filters)) if (v && v !== ALL && names.has(name)) out[name] = v;
  return out;
}
