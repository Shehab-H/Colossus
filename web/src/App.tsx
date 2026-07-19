import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import DeckGL from '@deck.gl/react';
import { COORDINATE_SYSTEM, OrthographicView, type PickingInfo } from '@deck.gl/core';
import { ScatterplotLayer, SolidPolygonLayer } from '@deck.gl/layers';
import { DataFilterExtension } from '@deck.gl/extensions';
import { MapboxOverlay, type MapboxOverlayProps } from '@deck.gl/mapbox';
// Aliased: a bare `Map` import would shadow the built-in Map constructor.
import { Map as BaseMap, useControl } from 'react-map-gl/maplibre';
import 'maplibre-gl/dist/maplibre-gl.css';
import Hud from './components/Hud';
import Controls from './components/Controls';
import PerfDashboard from './components/PerfDashboard';
import { isPerfOn, recordDeck, timedSync } from './lib/perf';
import { panel } from './components/controlStyles';
import InspectPanel, { type Selection } from './components/InspectPanel';
import LegendBox from './components/Legend';
import ThemeToggle from './components/ThemeToggle';
import { useTiles, type RenderedTile } from './hooks/useTiles';
import { useViewData } from './hooks/useViewData';
import { foldActive, useMeasureFold } from './hooks/useMeasureFold';
import { basemapStyle } from './lib/basemap';
import { useTheme } from './lib/theme';
import { buildEmbedUrl, embedSnippet, readEmbedParams } from './lib/embed';
import { tileDeckData } from './lib/deckData';
import type { Manifest } from './lib/manifest';
import { columnValue, type TileData } from './lib/tileData';
import { isSlab } from './lib/slab';
import { carriedFilterableChannels, colorableChannels, splitFilters } from './lib/channels';
import { filterSlots, filterRanges, anyActive } from './lib/gpuFilter';
import { colorScaleExtension } from './lib/colorScaleExtension';
import { coverTiles } from './lib/tiling';
import { initialViewState, type CameraState } from './lib/viewport';
import { listViews, setUrlViewId, urlViewId, type ViewSummary } from './lib/views';

// One DataFilterExtension per filterSize, shared across all layers. A fresh instance per render would
// defeat deck's prop diffing (it compares extension identity), forcing a shader relink each frame.
const filterExtensions = new Map<number, DataFilterExtension>();
function dataFilterExtensionFor(size: number): DataFilterExtension {
  let ext = filterExtensions.get(size);
  if (!ext) {
    ext = new DataFilterExtension({ filterSize: size as 1 | 2 | 3 | 4 });
    filterExtensions.set(size, ext);
  }
  return ext;
}

// deck.gl layers as a MapLibre control, overlaid on the base map. Geo views mount MapLibre as the
// root so the map owns camera + canvas sizing; the marks ride on top through this overlay.
function DeckOverlay(props: MapboxOverlayProps) {
  const overlay = useControl(() => new MapboxOverlay(props));
  overlay.setProps(props);
  return null;
}

// One stable deck layer id per tile (never folds in measure/filter — see the layers memo).
const layerId = (viewId: string | null, version: string, key: string) => `t-${viewId}-${version}-${key}`;

// Constant fill for every mark — the color extension overwrites rgb from the LUT in the vertex shader,
// so no per-vertex color array exists anywhere. Module-level for stable prop identity.
const WHITE: [number, number, number, number] = [255, 255, 255, 255];

const inspectValue = (v: number | string | undefined): string =>
  v === undefined ? '—' : typeof v === 'number' ? (Number.isInteger(v) ? String(v) : v.toFixed(2)) : v;

export default function App() {
  const embed = useMemo(() => readEmbedParams(), []);
  const chrome = !embed.embed; // embed=1 → chromeless: only the map + legend
  const { theme, toggle } = useTheme();
  const [views, setViews] = useState<ViewSummary[]>([]);
  const [viewsError, setViewsError] = useState<string | null>(null);
  const [viewId, setViewId] = useState<string | null>(urlViewId());
  const [camera, setCamera] = useState<CameraState | null>(null);
  const [size, setSize] = useState({ width: window.innerWidth, height: window.innerHeight });

  useEffect(() => {
    const onResize = () => setSize({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  // The registry drives the picker; a ?view= deep link keeps working even without the API.
  useEffect(() => {
    listViews()
      .then((vs) => {
        setViews(vs);
        setViewId((cur) => cur ?? (vs.find((v) => v.baked) ?? vs[0])?.id ?? null);
      })
      .catch(() => setViewsError('view registry unreachable — start the server or open ?view=<id>'));
  }, []);

  const selectView = (id: string) => {
    setViewId(id);
    setUrlViewId(id);
  };

  const initial = useMemo(() => ({ color: embed.color, colorSpec: embed.colorSpec, filters: embed.filters }), [embed]);
  const { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, renderChannel, colorLut, legend, activeFilters } =
    useViewData(viewId, initial);
  const [selection, setSelection] = useState<Selection | null>(null);

  // Reset the camera in the same render the manifest lands, so tile selection never sees a new
  // manifest through the previous view's camera. An embed URL's camera (geo) overrides the default
  // framing. A new manifest also drops any pinned inspection.
  const [prevManifest, setPrevManifest] = useState<Manifest | null>(null);
  if (manifest !== prevManifest) {
    setPrevManifest(manifest);
    const base = manifest ? initialViewState(manifest, size) : null;
    setCamera(base && embed.camera && manifest!.view.viewport === 'geo' ? { ...base, ...embed.camera } : base);
    setSelection(null);
  }

  // GPU filter slots (one per filterable channel) — the tile identity that rides into the worker so each
  // tile bakes its filter attribute once; not part of the cache key. Recomputed only when the manifest
  // or its options change, so it stays a stable object across filter changes.
  const slots = useMemo(() => (manifest ? filterSlots(manifest, options) : null), [manifest, options]);

  // On a packed bake the render needs one column beyond first paint: the active colour channel. Inspect
  // channels stay lazy and load per click (see onPick) — fetching them for every resident tile would
  // defeat the pack.
  const renderChannels = useMemo(() => (renderChannel ? [renderChannel] : []), [renderChannel]);
  const { selKeys, rendered: loaded, marksLoaded, atFullFidelity, loadError, cacheGauge, ensureColumns } =
    useTiles(manifest, camera, size, slots, renderChannels);

  // deck publishes its own metrics once a second (GPU buffer/texture residency, GPU vs CPU frame time,
  // attribute-upload cost). Passed only when armed: the prop is what makes deck collect them at all, so
  // an unflagged session doesn't pay for stats it will never show. Stable identity — a fresh closure each
  // render would defeat deck's prop diffing.
  const onMetrics = useMemo(
    () => (isPerfOn() ? (m: Record<string, number>) => recordDeck(m as unknown as Parameters<typeof recordDeck>[0]) : undefined),
    [],
  );

  // A filter is a GPU predicate (perMark → tile decode/uniforms, exactly as the row regime) or a fold
  // context (perFact → recompute measures over the surviving facts). Row-regime views split all-predicate.
  const { predicate: predicateFilters, context: contextFilters } = useMemo(
    () => (manifest ? splitFilters(manifest, activeFilters) : { predicate: {}, context: {} }),
    [manifest, activeFilters],
  );

  // The tile set drawn last frame, read during render and committed after (effect below): under an
  // active context a tile already on screen keeps drawing until its replacement is drawable, so neither
  // a zoom nor a filter change ever blanks covered ground.
  const drawnRef = useRef<RenderedTile[]>([]);

  // Fold input: every loaded tile that may draw — the load cover plus any still-drawn stand-ins. A
  // retained parent stays folded (and keeps its fold-cache residency) until it actually leaves the
  // screen; once it does, the next pass lets its caches go.
  const foldInput = useMemo(() => {
    const seen = new Set(loaded.map((t) => t.key));
    return [...loaded, ...drawnRef.current.filter((t) => !seen.has(t.key))];
  }, [loaded]);

  // Per-tile folded measure columns under the active context, or null when there is no context (colour
  // straight from the baked default-context columns). Derived buffers are keyed by the context that
  // PRODUCED the fold (folded.contextSig), never the live selection — an in-flight fold would otherwise
  // cache the previous context's colours under the new key and serve them forever.
  // The map colours by one measure, so the slab fold fetches only that measure's planes (R1/R5 plane
  // split); a non-measure colour channel passes nothing and the fold falls back to every measure.
  const mapMeasures = useMemo(() => (renderChannel ? [renderChannel] : []), [renderChannel]);
  const { folded, foldInspect } = useMeasureFold(manifest, foldInput, contextFilters, mapMeasures);
  const contextKey = folded?.contextSig;
  const contextActive = foldActive(manifest, contextFilters);

  // What actually draws. No context: the load cover as-is. Active context: re-cover the selection with
  // fold-aware readiness — a tile draws once its fold has landed (or its companion is known missing,
  // the baked fallback), and a tile drawn last frame stays drawable, so the old cover holds the screen
  // until the new one is ready instead of flashing default-context colours or a gap.
  const drawn = useMemo<RenderedTile[]>(() => {
    if (!contextActive) return loaded;
    const byKey = new Map(foldInput.map((t) => [t.key, t]));
    const prev = new Set(drawnRef.current.map((t) => t.key));
    const ready = (k: string) =>
      byKey.has(k) && (folded ? folded.byTile.has(k) || folded.missing.has(k) : prev.has(k));
    return coverTiles(selKeys, ready).map((k) => byKey.get(k)!);
  }, [contextActive, loaded, foldInput, folded, selKeys]);

  useEffect(() => {
    drawnRef.current = drawn;
  });

  // GPU state that rides on the layers, never on tile identity: the color LUT (Phase 2) and the filter
  // uniforms (Phase 1). A measure/scale/theme change or a filter change makes new layer instances with the
  // SAME id and SAME `data` object — deck diffs props and updates a texture / uniforms, re-uploading no
  // per-mark data. Extension order is [dataFilter, colorScale] (they inject different shader stages).
  const gpuProps = useMemo(() => {
    const extensions = [];
    const props: Record<string, unknown> = {};
    if (slots) {
      extensions.push(dataFilterExtensionFor(slots.size));
      // Only predicate (perMark) filters are GPU-side; perFact context filters drive the fold, never a
      // filterRange (their slots stay open, so no mark is discarded for a context selection).
      const ranges = filterRanges(slots, predicateFilters);
      props.filterRange = slots.size === 1 ? ranges[0] : ranges; // filterSize 1 → flat [min,max]; >1 → per-slot
      props.filterEnabled = anyActive(ranges);
    }
    if (colorLut) {
      extensions.push(colorScaleExtension);
      props.getFillColor = WHITE; // constant; the color extension overwrites rgb from the LUT per mark
      props.scaleLut = colorLut;
      // Under an active fold context a mark with no surviving facts (folded NaN / ARGMAX_UNKNOWN)
      // is discarded on the GPU — it disappears like a predicate-filtered mark, instead of painting
      // the unknown colour over ground the filter excluded.
      props.scaleDiscardUnknown = contextActive;
    }
    return { ...props, extensions };
  }, [slots, predicateFilters, colorLut, contextActive]);

  // Categorical color channels feed canonical codes into the value attribute (numeric → null). Stable
  // per channel, so a scale/theme change never rebuilds the tile `data`.
  const colorCategories = useMemo(() => (colorLut?.kind === 'categorical' ? colorLut.categories ?? null : null), [colorLut]);

  const isGeo = manifest?.view.viewport === 'geo';
  const isPolygon = manifest?.view.mark === 'polygon';
  const inspect = manifest?.view.inspect;

  // Layer id → its tile (data + key), so a pick can read the clicked mark's channel values (deck returns
  // the mark index, not the row, for binary layers) and its folded measures under the active context.
  const dataByLayerId = useMemo(() => {
    const m = new Map<string, { data: TileData; key: string }>();
    if (manifest) for (const { key, data } of drawn) m.set(layerId(viewId, manifest.version, key), { data, key });
    return m;
  }, [drawn, viewId, manifest]);

  const onPick = useCallback(
    async (info: PickingInfo) => {
      if (!inspect) return;
      const hit = info.layer ? dataByLayerId.get(info.layer.id) : undefined;
      const i = info.index;
      if (!hit || i == null || i < 0) {
        setSelection(null); // click on empty map dismisses
        return;
      }
      // A measure under active context reads its folded value (numeric, or an argmax code decoded through
      // its category domain); everything else, and the no-context case, reads the baked tile column. The
      // map fold only carries the colour measure, so a slab tooltip fetches the rest of its inspect planes
      // on demand (foldInspect); older paths read the whole-tile fold as before.
      const cols =
        manifest && isSlab(manifest) && contextActive ? await foldInspect(hit.key) : folded?.byTile.get(hit.key);
      // Packed bake: the inspect columns were never fetched for this tile (that is the point — geonames'
      // `name` is ~44% of its bytes). Range them now, for this one tile, and read the merged result.
      const names = [...inspect.channels, ...(inspect.title ? [inspect.title] : [])];
      const data = (await ensureColumns(hit.key, names)) ?? hit.data;
      const valueAt = (name: string) => {
        const fc = cols?.[name];
        if (!fc) return inspectValue(columnValue(data.values[name], i));
        const v = fc[i];
        if (fc instanceof Uint16Array) {
          const cats = manifest?.channelDomains?.[name]?.values;
          return inspectValue(cats && v < cats.length ? cats[v] : undefined);
        }
        return inspectValue(Number.isNaN(v) ? undefined : v);
      };
      setSelection({
        title: inspect.title ? valueAt(inspect.title) : undefined,
        rows: inspect.channels.map((name) => ({ name, value: valueAt(name) })),
      });
    },
    [inspect, dataByLayerId, folded, manifest, contextActive, foldInspect, ensureColumns],
  );

  const orthographicView = useMemo(
    () => new OrthographicView({ id: 'main', controller: true, flipY: false }),
    [],
  );

  // Build a shareable embed for the current view state (called by the HUD's Embed button at click time,
  // so it captures the live camera / color / theme / filters).
  const getEmbed = useCallback(() => {
    const base = window.location.origin + window.location.pathname;
    const cam =
      isGeo && camera
        ? { longitude: Number(camera.longitude), latitude: Number(camera.latitude), zoom: Number(camera.zoom) }
        : null;
    const url = buildEmbedUrl(base, { view: viewId ?? '', color: colorChannel, theme, camera: cam, filters });
    return { url, snippet: embedSnippet(url) };
  }, [isGeo, camera, viewId, colorChannel, theme, filters]);

  const layers = useMemo(() => {
    if (!manifest) return [];
    const coordinateSystem = isGeo ? COORDINATE_SYSTEM.LNGLAT : COORDINATE_SYSTEM.CARTESIAN;
    const pickable = !!inspect; // marks answer clicks only when the view opts into inspection
    return timedSync('layers', () => drawn.map(({ key, data }) => {
      // Key by tile ONLY — never by measure/filter. A stable id lets deck match the existing layer
      // and swap attribute buffers in place; folding measure into the id destroyed and rebuilt every
      // on-screen layer per picker switch, re-running geometry setup for ~1M cells in one frame.
      const id = layerId(viewId, manifest.version, key);
      if (isPolygon) {
        return new SolidPolygonLayer({
          id,
          // Binary layout: flat vertices + per-polygon start offsets. No object accessors, so nothing
          // runs per-cell on the main thread — it uploads straight to the GPU. The GPU color value
          // (getScaleValue) and filter (getFilterValue) attributes ride in data.attributes (per-vertex).
          data: tileDeckData(data, renderChannel, colorCategories, slots?.size, folded?.byTile.get(key)?.[renderChannel], contextKey) as never,
          _normalize: false, // rings are already simple + consistent from the bake
          positionFormat: 'XY',
          coordinateSystem,
          opacity: 0.85,
          pickable,
          ...gpuProps,
          // autoHighlight is intentionally OFF: it forces a full picking pass + synchronous
          // gl.readPixels on every pointermove (re-rasterizing every on-screen mark), which is the
          // dominant pan/hover stall. Click still picks on demand via onClick — inspection is intact.
          autoHighlight: false,
        });
      }
      return new ScatterplotLayer({
        id,
        data: tileDeckData(data, renderChannel, colorCategories, slots?.size) as never,
        coordinateSystem,
        radiusUnits: 'pixels',
        getRadius: 1.6,
        radiusMinPixels: 1,
        radiusMaxPixels: 3,
        stroked: false,
        opacity: 0.85,
        pickable,
        ...gpuProps,
        autoHighlight: false, // see the polygon layer above — per-pointermove picking is the pan stall
      });
    }), (ls) => ({ n: ls.length }));
  }, [drawn, viewId, manifest, isGeo, isPolygon, renderChannel, colorCategories, inspect, slots, gpuProps, folded, contextKey]);

  return (
    <div style={{ position: 'absolute', inset: 0, background: 'var(--app-bg)' }}>
      {/* Geo views: MapLibre is the root (owns camera + sizing); deck marks overlay on top. */}
      {isGeo && camera && (
        <BaseMap
          mapStyle={basemapStyle(theme)}
          {...(camera as object)}
          onMove={(e: { viewState: CameraState }) => setCamera(e.viewState)}
          onLoad={(e: { target: { resize: () => void } }) => e.target.resize()}
          style={{ position: 'absolute', inset: 0 }}
        >
          <DeckOverlay layers={layers} onClick={onPick} _onMetrics={onMetrics} />
        </BaseMap>
      )}

      {/* Non-geo views: deck.gl is the root with an orthographic camera, no base map. */}
      {!isGeo && camera && (
        <DeckGL
          views={orthographicView}
          viewState={camera as never}
          controller
          onViewStateChange={(e: { viewState: CameraState }) => setCamera(e.viewState)}
          onClick={onPick}
          layers={layers}
          _onMetrics={onMetrics}
        />
      )}

      {chrome && (
        <Hud
          views={views}
          viewId={viewId}
          onViewChange={selectView}
          error={error ?? loadError ?? (viewId ? null : viewsError)}
          colorChannels={manifest ? colorableChannels(manifest) : []}
          colorChannel={colorChannel}
          onColorChannelChange={setColorChannel}
          channels={manifest ? carriedFilterableChannels(manifest) : []}
          options={options}
          filters={filters}
          onFilterChange={(name, value) => setFilters((f) => ({ ...f, [name]: value }))}
          manifest={manifest}
          tilesInView={selKeys.length}
          marksLoaded={marksLoaded}
          atFullFidelity={atFullFidelity}
          getEmbed={manifest ? getEmbed : undefined}
        />
      )}

      {/* Showcase embed: a slim controls panel (color-by + filters) with no dataset tabs, branding, or
          stats — so an iframed map stays interactive for viewers without exposing the rest of the app.
          `controls=0` drops even this, locking the map to a static snapshot. */}
      {embed.embed && embed.controls && manifest && (
        <div style={panel}>
          <Controls
            colorChannels={colorableChannels(manifest)}
            colorChannel={colorChannel}
            onColorChannelChange={setColorChannel}
            channels={carriedFilterableChannels(manifest)}
            options={options}
            filters={filters}
            onFilterChange={(name, value) => setFilters((f) => ({ ...f, [name]: value }))}
          />
        </div>
      )}

      {selection && <InspectPanel selection={selection} onClose={() => setSelection(null)} />}
      {manifest && legend && <LegendBox legend={legend} />}
      {chrome && <ThemeToggle theme={theme} onToggle={toggle} />}

      {/* ?perf=1 — the live post-bake lifecycle monitor. Mounts in the embed too: an iframed showcase is
          a legitimate thing to measure, and the flag is explicit either way. */}
      {isPerfOn() && <PerfDashboard manifest={manifest} tilesInView={selKeys.length} cache={cacheGauge} />}
    </div>
  );
}
