import { useCallback, useEffect, useMemo, useState } from 'react';
import DeckGL from '@deck.gl/react';
import { COORDINATE_SYSTEM, OrthographicView, type PickingInfo } from '@deck.gl/core';
import { ScatterplotLayer, SolidPolygonLayer } from '@deck.gl/layers';
import { DataFilterExtension } from '@deck.gl/extensions';
import { MapboxOverlay, type MapboxOverlayProps } from '@deck.gl/mapbox';
// Aliased: a bare `Map` import would shadow the built-in Map constructor.
import { Map as BaseMap, useControl } from 'react-map-gl/maplibre';
import 'maplibre-gl/dist/maplibre-gl.css';
import Hud from './components/Hud';
import InspectPanel, { type Selection } from './components/InspectPanel';
import LegendBox from './components/Legend';
import ThemeToggle from './components/ThemeToggle';
import { useTiles } from './hooks/useTiles';
import { useViewData } from './hooks/useViewData';
import { useMeasureFold } from './hooks/useMeasureFold';
import { basemapStyle } from './lib/basemap';
import { useTheme } from './lib/theme';
import { buildEmbedUrl, embedSnippet, readEmbedParams } from './lib/embed';
import { tileDeckData } from './lib/deckData';
import type { Manifest } from './lib/manifest';
import { columnValue, type TileData } from './lib/tileData';
import { carriedFilterableChannels, colorableChannels, splitFilters } from './lib/channels';
import { filterSlots, filterRanges, anyActive } from './lib/gpuFilter';
import { colorScaleExtension } from './lib/colorScaleExtension';
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
  const { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, colorLut, legend, activeFilters } =
    useViewData(viewId, initial);
  const [selection, setSelection] = useState<Selection | null>(null);

  // Reset the camera in the same render the manifest lands, so tile selection never sees a new
  // manifest through the previous view's camera. An embed URL's camera (geo) overrides the default
  // framing. A new manifest also drops any pinned inspection.
  const [prevManifest, setPrevManifest] = useState<Manifest | null>(null);
  if (manifest !== prevManifest) {
    setPrevManifest(manifest);
    const base = manifest ? initialViewState(manifest) : null;
    setCamera(base && embed.camera && manifest!.view.viewport === 'geo' ? { ...base, ...embed.camera } : base);
    setSelection(null);
  }

  // GPU filter slots (one per filterable channel) — the tile identity that rides into the worker so each
  // tile bakes its filter attribute once; not part of the cache key. Recomputed only when the manifest
  // or its options change, so it stays a stable object across filter changes.
  const slots = useMemo(() => (manifest ? filterSlots(manifest, options) : null), [manifest, options]);

  const { selKeys, rendered, marksLoaded, atFullFidelity, loadError } = useTiles(manifest, camera, size, slots);

  // A filter is a GPU predicate (perMark → tile decode/uniforms, exactly as the row regime) or a fold
  // context (perFact → recompute measures over the surviving facts). Row-regime views split all-predicate.
  const { predicate: predicateFilters, context: contextFilters } = useMemo(
    () => (manifest ? splitFilters(manifest, activeFilters) : { predicate: {}, context: {} }),
    [manifest, activeFilters],
  );

  // Per-tile folded measure columns under the active context, or null when there is no context (colour
  // straight from the baked default-context columns). Derived buffers are keyed by the context that
  // PRODUCED the fold (folded.contextSig), never the live selection — an in-flight fold would otherwise
  // cache the previous context's colours under the new key and serve them forever.
  const folded = useMeasureFold(manifest, rendered, contextFilters);
  const contextKey = folded?.contextSig;

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
    }
    return { ...props, extensions };
  }, [slots, predicateFilters, colorLut]);

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
    if (manifest) for (const { key, data } of rendered) m.set(layerId(viewId, manifest.version, key), { data, key });
    return m;
  }, [rendered, viewId, manifest]);

  const onPick = useCallback(
    (info: PickingInfo) => {
      if (!inspect) return;
      const hit = info.layer ? dataByLayerId.get(info.layer.id) : undefined;
      const i = info.index;
      if (!hit || i == null || i < 0) {
        setSelection(null); // click on empty map dismisses
        return;
      }
      // A measure under active context reads its folded value (numeric, or an argmax code decoded through
      // its category domain); everything else, and the no-context case, reads the baked tile column.
      const cols = folded?.byTile.get(hit.key);
      const valueAt = (name: string) => {
        const fc = cols?.[name];
        if (!fc) return inspectValue(columnValue(hit.data.values[name], i));
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
    [inspect, dataByLayerId, folded, manifest],
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
    return rendered.map(({ key, data }) => {
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
          data: tileDeckData(data, colorChannel, colorCategories, slots?.size, folded?.byTile.get(key)?.[colorChannel], contextKey) as never,
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
        data: tileDeckData(data, colorChannel, colorCategories, slots?.size) as never,
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
    });
  }, [rendered, viewId, manifest, isGeo, isPolygon, colorChannel, colorCategories, inspect, slots, gpuProps, folded, contextKey]);

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
          <DeckOverlay layers={layers} onClick={onPick} />
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

      {selection && <InspectPanel selection={selection} onClose={() => setSelection(null)} />}
      {manifest && legend && <LegendBox legend={legend} />}
      {chrome && <ThemeToggle theme={theme} onToggle={toggle} />}
    </div>
  );
}
