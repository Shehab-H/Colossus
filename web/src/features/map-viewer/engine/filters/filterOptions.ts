import type { Manifest } from '@/types/manifest';
import { carriedFilterableChannels } from './filterRouting';
import type { ChannelSpec } from '@/types/viewConfig';
import { filterControl } from './filterControls';
import { isNumericChannel } from '../view/channelRoles';
import { fetchArrowTable } from '../network/arrowFetch';
import { tileUrl } from '../network/tileUrls';

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
    if (filterControl(manifest.view, ch) === 'range' && isNumericChannel(ch) &&
        baked?.min !== undefined && baked.max !== undefined)
      options[ch.name] = [String(baked.min), String(baked.max)];
    else if (baked?.values && !baked.valuesTruncated) options[ch.name] = baked.values;
    else missing.push(ch);
  }
  if (missing.length === 0) return options;

  const { table: t } = await fetchArrowTable(tileUrl(manifest.view.id, manifest.version, 0, 0, 0));
  for (const ch of missing) {
    const col = t.getChild(ch.name);
    if (filterControl(manifest.view, ch) === 'range' && isNumericChannel(ch)) {
      let min = Infinity;
      let max = -Infinity;
      if (col) for (let i = 0; i < t.numRows; i++) {
        const value = Number(col.get(i));
        if (!Number.isFinite(value)) continue;
        min = Math.min(min, value);
        max = Math.max(max, value);
      }
      options[ch.name] = Number.isFinite(min) ? [String(min), String(max)] : [];
      continue;
    }
    const set = new Set<string>();
    if (col) for (let i = 0; i < t.numRows; i++) set.add(String(col.get(i)));
    options[ch.name] = [...set].sort();
  }
  return options;
}
