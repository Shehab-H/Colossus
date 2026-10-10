import type { ViewConfig } from '@/types/viewConfig';
import { measureNames, isGroupRegime, measureChannels, measureChannelSpecs, isNumericChannel } from './channelRoles';
import type { Manifest } from '@/types/manifest';
import type { ColorDomain } from '@/lib/color/scale';
import { fetchArrowTable } from '../network/arrowFetch';
import { tileUrl } from '../network/tileUrls';

/** The channel a view colors by initially: the authored `encoding.color.channel` if it names a channel
 *  or a measure, else the first measure (group regime) / first measure channel, else the first channel. */
export const colorChannelName = (view: ViewConfig): string => {
  const authored = view.encoding?.color?.channel;
  const names = new Set<string>([...view.source.channels.map((c) => c.name), ...measureNames(view)]);
  if (authored && names.has(authored)) return authored;
  if (isGroupRegime(view)) return measureNames(view)[0] ?? view.source.channels[0]?.name ?? 'value';
  return measureChannels(view)[0]?.name ?? view.source.channels[0]?.name ?? 'value';
};

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
