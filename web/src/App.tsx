import { useCallback, useEffect, useMemo, useState } from 'react';
import DeckGL from '@deck.gl/react';
import { COORDINATE_SYSTEM, OrthographicView, type PickingInfo } from '@deck.gl/core';
import { ScatterplotLayer, SolidPolygonLayer } from '@deck.gl/layers';
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
import { basemapStyle } from './lib/basemap';
import { useTheme } from './lib/theme';
import { tileDeckData } from './lib/deckData';
import type { Manifest } from './lib/manifest';
import type { TileData } from './lib/tileData';
import { colorableChannels, filterableChannels } from './lib/channels';
import { initialViewState, type CameraState } from './lib/viewport';
import { listViews, setUrlViewId, urlViewId, type ViewSummary } from './lib/views';

// deck.gl layers as a MapLibre control, overlaid on the base map. Geo views mount MapLibre as the
// root so the map owns camera + canvas sizing; the marks ride on top through this overlay.
function DeckOverlay(props: MapboxOverlayProps) {
  const overlay = useControl(() => new MapboxOverlay(props));
  overlay.setProps(props);
  return null;
}

// One stable deck layer id per tile (never folds in measure/filter — see the layers memo).
const layerId = (viewId: string | null, version: string, key: string) => `t-${viewId}-${version}-${key}`;

const inspectValue = (v: number | string | undefined): string =>
  v === undefined ? '—' : typeof v === 'number' ? (Number.isInteger(v) ? String(v) : v.toFixed(2)) : v;

export default function App() {
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

  const { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, colorOf, scaleKey, legend, filterSql } =
    useViewData(viewId);
  const [selection, setSelection] = useState<Selection | null>(null);

  // Reset the camera in the same render the manifest lands, so tile selection never sees a new
  // manifest through the previous view's camera. A new manifest also drops any pinned inspection.
  const [prevManifest, setPrevManifest] = useState<Manifest | null>(null);
  if (manifest !== prevManifest) {
    setPrevManifest(manifest);
    setCamera(manifest ? initialViewState(manifest) : null);
    setSelection(null);
  }

  const { selKeys, rendered, marksLoaded, atFullFidelity, loadError } = useTiles(manifest, camera, size, filterSql);

  const isGeo = manifest?.view.viewport === 'geo';
  const isPolygon = manifest?.view.mark === 'polygon';
  const inspect = manifest?.view.inspect;

  // Layer id → its tile data, so a pick can read the clicked mark's channel values (deck returns the
  // mark index, not the row, for binary layers).
  const dataByLayerId = useMemo(() => {
    const m = new Map<string, TileData>();
    if (manifest) for (const { key, data } of rendered) m.set(layerId(viewId, manifest.version, key), data);
    return m;
  }, [rendered, viewId, manifest]);

  const onPick = useCallback(
    (info: PickingInfo) => {
      if (!inspect) return;
      const tile = info.layer ? dataByLayerId.get(info.layer.id) : undefined;
      const i = info.index;
      if (!tile || i == null || i < 0) {
        setSelection(null); // click on empty map dismisses
        return;
      }
      setSelection({
        title: inspect.title ? inspectValue(tile.values[inspect.title]?.[i]) : undefined,
        rows: inspect.channels.map((name) => ({ name, value: inspectValue(tile.values[name]?.[i]) })),
      });
    },
    [inspect, dataByLayerId],
  );

  const orthographicView = useMemo(
    () => new OrthographicView({ id: 'main', controller: true, flipY: false }),
    [],
  );

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
          // Binary layout: flat vertices + per-polygon start offsets + per-vertex colors. No object
          // accessors, so nothing runs per-cell on the main thread — it uploads straight to the GPU.
          data: tileDeckData(data, colorChannel, colorOf, scaleKey) as never,
          _normalize: false, // rings are already simple + consistent from the bake
          positionFormat: 'XY',
          coordinateSystem,
          opacity: 0.85,
          pickable,
          autoHighlight: pickable,
        });
      }
      return new ScatterplotLayer({
        id,
        data: tileDeckData(data, colorChannel, colorOf, scaleKey) as never,
        coordinateSystem,
        radiusUnits: 'pixels',
        getRadius: 1.6,
        radiusMinPixels: 1,
        radiusMaxPixels: 3,
        stroked: false,
        opacity: 0.85,
        pickable,
        autoHighlight: pickable,
        highlightColor: [255, 255, 255, 120],
      });
    });
  }, [rendered, viewId, manifest, isGeo, isPolygon, colorChannel, colorOf, scaleKey, inspect]);

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

      <Hud
        views={views}
        viewId={viewId}
        onViewChange={selectView}
        error={error ?? loadError ?? (viewId ? null : viewsError)}
        colorChannels={manifest ? colorableChannels(manifest.view) : []}
        colorChannel={colorChannel}
        onColorChannelChange={setColorChannel}
        channels={manifest ? filterableChannels(manifest.view) : []}
        options={options}
        filters={filters}
        onFilterChange={(name, value) => setFilters((f) => ({ ...f, [name]: value }))}
        manifest={manifest}
        tilesInView={selKeys.length}
        marksLoaded={marksLoaded}
        atFullFidelity={atFullFidelity}
      />

      {selection && <InspectPanel selection={selection} onClose={() => setSelection(null)} />}
      {manifest && legend && <LegendBox legend={legend} />}
      <ThemeToggle theme={theme} onToggle={toggle} />
    </div>
  );
}
