import type { ChannelSpec, Manifest, ViewConfig } from './manifest';
import { tileUrl } from './manifest';
import { fetchArrowTable } from './arrow';
import { parseMeasure } from './measures';
import type { ColorDomain } from './colorScale';

// Date/ALL helpers live in dates.ts (dependency-free, so measures.ts can share them without a cycle);
// imported for internal use and re-exported so existing importers keep resolving them from './channels'.
import { ALL, RANGE_SEP, parseDateRange, makeDateRange, inDateRange, type DateRange } from './dates';
export { ALL, RANGE_SEP, parseDateRange, makeDateRange, inDateRange, type DateRange };

/** Channel types that carry a real number line (so continuous/binned color scales apply). Everything
 *  else (dict, date) is treated as discrete categories for coloring. */
export const NUMERIC_TYPES = new Set(['f32', 'f64', 'u8', 'u16', 'i32', 'i64']);

export const isNumericChannel = (ch: ChannelSpec | undefined): boolean => !!ch && NUMERIC_TYPES.has(ch.type);

/** Every measure-role source channel — carried in every row-regime tile, the usual choice to color by. */
export const measureChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'measure');

/** A view is in the group regime iff it declares measures (VIEW_CONFIG §1). */
export const isGroupRegime = (view: ViewConfig): boolean => (view.measures?.length ?? 0) > 0;

export const measureNames = (view: ViewConfig): string[] => view.measures?.map((m) => m.name) ?? [];

/** Each declared measure as a virtual channel: argmax/argmin colour categorically over the dimension's
 *  domain (dict); every other measure is numeric (f32). Mirrors the bake's effective view. */
export function measureChannelSpecs(view: ViewConfig): ChannelSpec[] {
  return (view.measures ?? []).map((m) => {
    const { kind } = parseMeasure(m.expr);
    const categorical = kind === 'argmax' || kind === 'argmin';
    return {
      name: m.name,
      column: m.name,
      role: categorical ? 'dimension' : 'measure',
      type: categorical ? 'dict' : 'f32',
    };
  });
}

/** The columns a render tile actually carries. Row regime: the source channels. Group regime: the
 *  effective marks view — the mark `id`, the perMark channels, and each materialized measure. */
export function renderChannels(manifest: Manifest): ChannelSpec[] {
  const view = manifest.view;
  if (!isGroupRegime(view)) return view.source.channels;
  const perMark = new Set(manifest.factChannels?.perMark ?? []);
  const id: ChannelSpec = { name: 'id', column: 'id', role: 'identity', type: 'dict' };
  return [id, ...view.source.channels.filter((c) => perMark.has(c.name)), ...measureChannelSpecs(view)];
}

/** The view the tile decoder should use. Row regime: the authored view. Group regime: the authored view
 *  with its channels swapped for the render channels, so a tile's measure/id columns decode with the
 *  right types (the authored perFact channels aren't in the tile at all). */
export function renderDecodeView(manifest: Manifest): ViewConfig {
  const view = manifest.view;
  if (!isGroupRegime(view)) return view;
  return { ...view, source: { ...view.source, channels: renderChannels(manifest) } };
}

/** Any channel a view can color by. Row regime: every carried channel. Group regime: the measures plus
 *  the perMark channels (a raw perFact channel has no single per-mark value to colour). */
export function colorableChannels(manifest: Manifest): ChannelSpec[] {
  const view = manifest.view;
  if (!isGroupRegime(view)) return view.source.channels;
  const perMark = new Set(manifest.factChannels?.perMark ?? []);
  return [...view.source.channels.filter((c) => perMark.has(c.name)), ...measureChannelSpecs(view)];
}

export const filterableChannels = (view: ViewConfig): ChannelSpec[] =>
  view.source.channels.filter((c) => c.role === 'dimension' || c.role === 'temporal');

/** The filterable channels a view can actually answer, so only live controls render. Group regime:
 *  all of them (perMark → GPU predicate, perFact → fold context). Row regime: the aggregate reducer
 *  drops dimension/temporal columns from its tiles (each cell averages every slice), so nothing is
 *  filterable — offering those controls would silently blank the view (every mark reads MISSING_CODE). */
export function carriedFilterableChannels(manifest: Manifest): ChannelSpec[] {
  if (isGroupRegime(manifest.view)) return filterableChannels(manifest.view);
  return manifest.reduction === 'aggregate' ? [] : filterableChannels(manifest.view);
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

/** The channel a view colors by initially: the authored `encoding.color.channel` if it names a channel
 *  or a measure, else the first measure (group regime) / first measure channel, else the first channel. */
export const colorChannelName = (view: ViewConfig): string => {
  const authored = view.encoding?.color?.channel;
  const names = new Set<string>([...view.source.channels.map((c) => c.name), ...measureNames(view)]);
  if (authored && names.has(authored)) return authored;
  if (isGroupRegime(view)) return measureNames(view)[0] ?? view.source.channels[0]?.name ?? 'value';
  return measureChannels(view)[0]?.name ?? view.source.channels[0]?.name ?? 'value';
};

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

/** The one canonical category order for a channel: the baked full-extract domain when trustworthy (no
 *  category can be missing), else null so the caller falls back to the same sorted option list the UI
 *  shows (`discoverOptions`). Codes everywhere — GPU filter slots and the Phase-2 color LUT — index THIS
 *  array, so color categories and filter codes can never disagree. */
export function canonicalCategories(manifest: Manifest, channel: string): string[] | null {
  const baked = manifest.channelDomains?.[channel];
  return baked?.values && !baked.valuesTruncated ? baked.values : null;
}

/** Describe a color channel's domain. Preferred source: the manifest's baked `channelDomains` — scanned
 *  from the FULL extract, so no category can be missing and no tile fetch happens at view load. Older
 *  manifests (or capped/truncated domains) fall back to scanning the root tile, exactly as before. */
export async function describeColorDomain(manifest: Manifest, channel: string): Promise<ColorDomain> {
  const view = manifest.view;
  const spec =
    view.source.channels.find((c) => c.name === channel) ??
    measureChannelSpecs(view).find((c) => c.name === channel);
  const numeric = isNumericChannel(spec);

  const baked = manifest.channelDomains?.[channel];
  if (numeric && baked?.min !== undefined && baked.max !== undefined)
    return { kind: 'numeric', min: baked.min, max: baked.max, sample: baked.quantiles };
  if (!numeric) {
    const canon = canonicalCategories(manifest, channel);
    if (canon) return { kind: 'categorical', categories: canon };
  }

  const { table: t } = await fetchArrowTable(tileUrl(view.id, manifest.version, 0, 0, 0));
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

/** Distinct values of each live filterable channel, for the filter controls. Baked manifest domains
 *  answer without any fetch; only channels the manifest can't answer (older bake, or truncated) scan
 *  the root tile — fetched once, lazily. */
export async function discoverOptions(manifest: Manifest): Promise<Record<string, string[]>> {
  const channels = carriedFilterableChannels(manifest);
  if (channels.length === 0) return {};

  const options: Record<string, string[]> = {};
  const missing: ChannelSpec[] = [];
  for (const ch of channels) {
    const baked = manifest.channelDomains?.[ch.name];
    if (baked?.values && !baked.valuesTruncated) options[ch.name] = baked.values;
    else missing.push(ch);
  }
  if (missing.length === 0) return options;

  const { table: t } = await fetchArrowTable(tileUrl(manifest.view.id, manifest.version, 0, 0, 0));
  for (const ch of missing) {
    const col = t.getChild(ch.name);
    const set = new Set<string>();
    if (col) for (let i = 0; i < t.numRows; i++) set.add(String(col.get(i)));
    options[ch.name] = [...set].sort();
  }
  return options;
}
