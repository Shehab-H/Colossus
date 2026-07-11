import { useEffect, useMemo, useRef, useState } from 'react';
import { loadManifest, type ColorSpec, type Manifest } from '../lib/manifest';
import { ALL, activeFilters as pickActive, colorChannelName, describeColorDomain, discoverOptions, filterableChannels } from '../lib/channels';
import { describeLegend, type ColorDomain } from '../lib/colorScale';
import { buildColorLut } from '../lib/colorLut';

const EMPTY_DOMAIN: ColorDomain = { kind: 'numeric', min: 0, max: 1 };

/** Optional startup overrides (from an embed URL): pin the color channel and/or pre-apply filters so an
 *  embedded map paints its intended state on the first frame instead of the authored default. */
export interface ViewDataInitial {
  color?: string | null;
  colorSpec?: Partial<ColorSpec> | null;
  filters?: Record<string, string>;
}

/** Loads a view's manifest and derives the data-shaping state around it: filter options + defaults, the
 *  active color channel, its observed domain, and the color scale built from the authored encoding. A
 *  polygon view defaults to one clean slice (first value of each dimension); a point view is unfiltered. */
export function useViewData(viewId: string | null, initial?: ViewDataInitial) {
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [options, setOptions] = useState<Record<string, string[]>>({});
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [colorChannel, setColorChannel] = useState('');
  const [domain, setDomain] = useState<ColorDomain>(EMPTY_DOMAIN);

  // Read the overrides fresh on each view load without making them an effect dependency.
  const initialRef = useRef(initial);
  initialRef.current = initial;

  useEffect(() => {
    if (!viewId) return;
    let alive = true;
    setError(null);
    loadManifest(viewId)
      .then(async (m) => {
        if (!alive) return;
        const seed = initialRef.current;
        setManifest(m);
        setColorChannel(seed?.color || colorChannelName(m.view));
        const o = await discoverOptions(m);
        if (!alive) return;
        setOptions(o);
        const defaults: Record<string, string> = {};
        for (const ch of filterableChannels(m.view)) {
          const opts = o[ch.name] ?? [];
          const pick = ch.role === 'temporal' ? opts[opts.length - 1] : opts[0];
          defaults[ch.name] = m.view.mark === 'polygon' ? pick ?? ALL : ALL;
        }
        setFilters({ ...defaults, ...seed?.filters });
      })
      .catch((e) => {
        if (alive) setError(`${viewId}: ${e instanceof Error ? e.message : 'not baked yet'}`);
      });
    return () => {
      alive = false;
    };
  }, [viewId]);

  // The domain re-samples from the root tile whenever the color channel changes, so a channel switch
  // rescales (numeric range, or the set of categories) correctly.
  useEffect(() => {
    if (!manifest || !colorChannel) return;
    let alive = true;
    describeColorDomain(manifest, colorChannel).then((d) => alive && setDomain(d));
    return () => {
      alive = false;
    };
  }, [manifest, colorChannel]);

  // The color spec: the authored encoding when it targets the active channel, else an inferred default
  // for whatever channel the user picked.
  const colorSpec = useMemo<ColorSpec>(() => {
    const authored = manifest?.view.encoding?.color;
    const base = authored && authored.channel === colorChannel ? authored : { channel: colorChannel };
    const ov = initialRef.current?.colorSpec;
    return ov && ov.channel === colorChannel ? { ...base, ...ov, channel: colorChannel } : base;
  }, [manifest, colorChannel]);

  // The GPU color LUT: colorScale.ts sampled into a texture + uniforms. Its identity changes exactly when
  // the mapping changes (measure/scale/theme), which is what re-uploads the texture; per-mark data never
  // moves. buildColorLut uses buildColorScale internally, so it can't disagree with the legend below.
  const colorLut = useMemo(() => buildColorLut(colorSpec, domain), [colorSpec, domain]);
  // Legend descriptor from the same scale, so a swatch can never disagree with a rendered mark.
  const legend = useMemo(() => (colorChannel ? describeLegend(colorSpec, domain, colorChannel) : null), [colorSpec, domain, colorChannel]);

  const activeFilters = useMemo(() => (manifest ? pickActive(manifest.view, filters) : {}), [filters, manifest]);

  return { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, colorSpec, colorLut, legend, activeFilters };
}
