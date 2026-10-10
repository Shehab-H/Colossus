import type { ChannelSpec, ViewConfig } from '@/types/viewConfig';
import { parseMeasure } from '../fold/measureGrammar';
import type { Manifest } from '@/types/manifest';

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
