import type { ChannelSpec, FilterSpec, ViewConfig } from '@/types/viewConfig';

export function defaultFilterControl(channel: ChannelSpec): FilterSpec['control'] | null {
  if (channel.role === 'temporal' || channel.type === 'date') return 'dateRange';
  if (channel.role === 'dimension' && channel.type === 'dict') return 'select';
  return null;
}

export function filterControl(view: ViewConfig, channel: ChannelSpec): FilterSpec['control'] | null {
  return view.filters?.find((filter) => filter.channel === channel.name)?.control ??
    (view.filters === undefined ? defaultFilterControl(channel) : null);
}
