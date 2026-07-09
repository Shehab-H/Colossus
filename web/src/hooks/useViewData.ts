import { useEffect, useMemo, useRef, useState } from 'react';
import { loadManifest, type ColorSpec, type Manifest } from '../lib/manifest';
import { ALL, buildWhere, colorChannelName, describeColorDomain, discoverOptions, filterableChannels } from '../lib/channels';
import { buildColorScale, describeLegend, type ColorDomain } from '../lib/colorScale';

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
        const o = await discoverOptions(m.view, m.version);
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
    describeColorDomain(manifest.view, manifest.version, colorChannel).then((d) => alive && setDomain(d));
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

  const colorOf = useMemo(() => buildColorScale(colorSpec, domain), [colorSpec, domain]);
  // A stable identity for the built scale, so the layers memo recolors exactly when the mapping changes.
  const scaleKey = useMemo(
    () => JSON.stringify([colorSpec, domain.kind, domain.kind === 'numeric' ? [domain.min, domain.max] : domain.categories]),
    [colorSpec, domain],
  );
  // Legend descriptor from the same scale, so a swatch can never disagree with a rendered mark.
  const legend = useMemo(() => (colorChannel ? describeLegend(colorSpec, domain, colorChannel) : null), [colorSpec, domain, colorChannel]);

  const filterSql = useMemo(() => (manifest ? buildWhere(manifest.view, filters) : ''), [filters, manifest]);

  return { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, colorSpec, colorOf, scaleKey, legend, filterSql };
}
