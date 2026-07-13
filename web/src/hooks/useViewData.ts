import { useEffect, useMemo, useRef, useState } from 'react';
import { loadManifest, type ColorSpec, type Manifest } from '../lib/manifest';
import { ALL, activeFilters as pickActive, carriedFilterableChannels, colorChannelName, describeColorDomain, discoverOptions, isGroupRegime } from '../lib/channels';
import { describeLegend, type ColorDomain } from '../lib/colorScale';
import { buildColorLut } from '../lib/colorLut';
import { activateTileVersion } from '../lib/swClient';

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
  // The scale the map actually renders with: channel + its observed domain, committed atomically once
  // the (async) domain describe lands. Rendering the picked channel against the PREVIOUS channel's
  // domain — even for one frame — builds wrong value buffers (a numeric channel read through a
  // categorical LUT codes every mark as `unknown`), so the old channel keeps drawing until its
  // replacement is whole.
  const [scale, setScale] = useState<{ channel: string; domain: ColorDomain } | null>(null);

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
        setScale(null); // a new view's channels invalidate any previous scale
        // Version rotation is the SW's cache GC: tell it which version is live now.
        activateTileVersion(m.view.id, m.version);
        setColorChannel(seed?.color || colorChannelName(m.view));
        const o = await discoverOptions(m);
        if (!alive) return;
        setOptions(o);
        // Row-regime polygon views default to one clean slice (overlapping slices per geometry would
        // overdraw). A group-regime view needs no slice — its marks are unique per geometry and its
        // measures are baked at the default context, so it starts unfiltered: no fold, zero extra work.
        const slice = m.view.mark === 'polygon' && !isGroupRegime(m.view);
        const defaults: Record<string, string> = {};
        for (const ch of carriedFilterableChannels(m)) {
          const opts = o[ch.name] ?? [];
          const pick = ch.role === 'temporal' ? opts[opts.length - 1] : opts[0];
          defaults[ch.name] = slice ? pick ?? ALL : ALL;
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

  // The domain re-samples from the manifest (or root tile) whenever the color channel changes; the
  // channel and its domain commit together, so the LUT can never belong to a different channel than
  // the value buffers.
  useEffect(() => {
    if (!manifest || !colorChannel) return;
    let alive = true;
    describeColorDomain(manifest, colorChannel).then((d) => alive && setScale({ channel: colorChannel, domain: d }));
    return () => {
      alive = false;
    };
  }, [manifest, colorChannel]);

  // Everything below derives from the committed scale, not the live selection: the channel the map
  // renders (`renderChannel`), its spec, LUT, and legend flip in ONE render when the new domain lands.
  const renderChannel = scale?.channel ?? '';

  // The color spec: the authored encoding when it targets the rendered channel, else an inferred
  // default for whatever channel the user picked.
  const colorSpec = useMemo<ColorSpec>(() => {
    const authored = manifest?.view.encoding?.color;
    const base = authored && authored.channel === renderChannel ? authored : { channel: renderChannel };
    const ov = initialRef.current?.colorSpec;
    return ov && ov.channel === renderChannel ? { ...base, ...ov, channel: renderChannel } : base;
  }, [manifest, renderChannel]);

  // The GPU color LUT: colorScale.ts sampled into a texture + uniforms. Its identity changes exactly when
  // the mapping changes (measure/scale/theme), which is what re-uploads the texture; per-mark data never
  // moves. buildColorLut uses buildColorScale internally, so it can't disagree with the legend below.
  const colorLut = useMemo(() => (scale ? buildColorLut(colorSpec, scale.domain) : null), [colorSpec, scale]);
  // Legend descriptor from the same scale, so a swatch can never disagree with a rendered mark.
  const legend = useMemo(
    () => (scale ? describeLegend(colorSpec, scale.domain, scale.channel) : null),
    [colorSpec, scale],
  );

  const activeFilters = useMemo(() => (manifest ? pickActive(manifest.view, filters) : {}), [filters, manifest]);

  return { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, renderChannel, colorSpec, colorLut, legend, activeFilters };
}
