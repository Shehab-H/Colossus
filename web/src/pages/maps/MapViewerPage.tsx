import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import DeckGL from '@deck.gl/react';
import { COORDINATE_SYSTEM, OrthographicView, type PickingInfo } from '@deck.gl/core';
import { ScatterplotLayer, SolidPolygonLayer } from '@deck.gl/layers';
import { DataFilterExtension } from '@deck.gl/extensions';
import { MapboxOverlay, type MapboxOverlayProps } from '@deck.gl/mapbox';
// Aliased: a bare `Map` import would shadow the built-in Map constructor.
import { Map as BaseMap, useControl } from 'react-map-gl/maplibre';
import 'maplibre-gl/dist/maplibre-gl.css';
import Controls from '@widgets/map-viewer/Controls';
import EmbedDisclosure from '@widgets/map-viewer/EmbedDisclosure';
import PerfDashboard from '@widgets/map-viewer/PerfDashboard';
import { isPerfOn, recordDeck, timedSync } from '@shared/observability/perf';
import InspectPanel, { type Selection } from '@widgets/map-viewer/InspectPanel';
import LegendBox from '@widgets/map-viewer/Legend';
import {
  EmbedShell,
  EmbedSidebar,
  EmbedStage,
  MapWindow,
  PanelSection,
  ViewerHeader,
  ViewerPanel,
  ViewerShell,
} from '@widgets/map-viewer/ViewerShell';
import ThemeToggle from '@shared/ui/ThemeToggle';
import { useTiles, type RenderedTile } from '@features/map-viewer/hooks/useTiles';
import { useViewData } from '@features/map-viewer/hooks/useViewData';
import { foldActive, useMeasureFold } from '@features/map-viewer/hooks/useMeasureFold';
import { basemapStyle } from '@features/map-viewer/engine/basemap';
import { useTheme } from '@shared/theme/useTheme';
import { buildEmbedUrl, embedSnippet, readEmbedParams } from '@features/map-viewer/engine/embed';
import { postEmbedEvent } from '@features/map-viewer/engine/embedMessages';
import { tileDeckData } from '@features/map-viewer/engine/deckData';
import type { Manifest } from '@domain/map/manifest';
import { columnValue, type TileData } from '@features/map-viewer/engine/tileData';
import { isSlab } from '@features/map-viewer/engine/slab';
import { carriedFilterableChannels, colorableChannels, splitFilters } from '@features/map-viewer/engine/channels';
import { filterSlots, filterRanges, anyActive } from '@features/map-viewer/engine/gpuFilter';
import { colorScaleExtension } from '@features/map-viewer/engine/colorScaleExtension';
import { coverTiles } from '@features/map-viewer/engine/tiling';
import { initialViewState, type CameraState } from '@features/map-viewer/engine/viewport';
import { replaceViewerState, type ViewerSurface } from '@features/map-viewer/model/viewRoute';

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

export default function MapViewerPage({ mapId, surface }: { mapId: string; surface: ViewerSurface }) {
  const initialState = useMemo(() => readEmbedParams(), []);
  const isEmbed = surface === 'embed';
  const { theme, toggle } = useTheme();
  const viewId = mapId;
  const [camera, setCamera] = useState<CameraState | null>(null);
  const [size, setSize] = useState({ width: window.innerWidth, height: window.innerHeight });
  const mapFrameRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const element = mapFrameRef.current;
    if (!element) return;
    const measure = () => {
      const rect = element.getBoundingClientRect();
      if (rect.width > 0 && rect.height > 0) setSize({ width: rect.width, height: rect.height });
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const initial = useMemo(
    () => ({ color: initialState.color, colorSpec: initialState.colorSpec, filters: initialState.filters }),
    [initialState],
  );
  const { manifest, error, options, filters, setFilters, colorChannel, setColorChannel, renderChannel, colorLut, legend, activeFilters } =
    useViewData(viewId, initial);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [controlsOpen, setControlsOpen] = useState(true);
  const [detailsOpen, setDetailsOpen] = useState(true);

  // Reset the camera in the same render the manifest lands, so tile selection never sees a new
  // manifest through the previous view's camera. An embed URL's camera (geo) overrides the default
  // framing. A new manifest also drops any pinned inspection.
  const [prevManifest, setPrevManifest] = useState<Manifest | null>(null);
  if (manifest !== prevManifest) {
    setPrevManifest(manifest);
    const base = manifest ? initialViewState(manifest, size) : null;
    setCamera(base && initialState.camera && manifest!.view.viewport === 'geo' ? { ...base, ...initialState.camera } : base);
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
  const { selKeys, rendered: loaded, marksLoaded, loadError, cacheGauge, ensureColumns } =
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
        if (isEmbed) postEmbedEvent({ type: 'selection', mapId, selection: null });
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
      const nextSelection = {
        title: inspect.title ? valueAt(inspect.title) : undefined,
        rows: inspect.channels.map((name) => ({ name, value: valueAt(name) })),
      };
      setSelection(nextSelection);
      if (isEmbed) postEmbedEvent({ type: 'selection', mapId, selection: nextSelection });
    },
    [inspect, dataByLayerId, folded, manifest, contextActive, foldInspect, ensureColumns, isEmbed, mapId],
  );

  const orthographicView = useMemo(
    () => new OrthographicView({ id: 'main', controller: true, flipY: false }),
    [],
  );

  // Build a shareable embed for the current view state (called by the shell's Embed button at click time,
  // so it captures the live camera / color / theme / filters).
  const getEmbed = useCallback(() => {
    const base = window.location.origin;
    const cam =
      isGeo && camera
        ? { longitude: Number(camera.longitude), latitude: Number(camera.latitude), zoom: Number(camera.zoom) }
        : null;
    const url = buildEmbedUrl(base, { view: viewId, color: colorChannel, theme, camera: cam, filters });
    return { url, snippet: embedSnippet(url) };
  }, [isGeo, camera, viewId, colorChannel, theme, filters]);

  // Live controls are part of a full viewer's addressable state. Camera is committed on interaction end
  // below so a pan does not churn history on every animation frame.
  useEffect(() => {
    if (isEmbed || !manifest) return;
    const filterChannels = carriedFilterableChannels(manifest);
    if (filterChannels.length > 0 && Object.keys(options).length === 0) return;
    replaceViewerState({ color: colorChannel, theme, filters });
  }, [isEmbed, manifest, options, colorChannel, theme, filters]);

  useEffect(() => {
    if (isEmbed && manifest) {
      postEmbedEvent({ type: 'ready', mapId, release: manifest.version });
    }
  }, [isEmbed, manifest, mapId]);

  useEffect(() => {
    const message = error ?? loadError;
    if (isEmbed && message) postEmbedEvent({ type: 'error', mapId, message });
  }, [isEmbed, error, loadError, mapId]);

  // Camera messages are deliberately settled, not per-frame. This applies to MapLibre and DeckGL and
  // keeps a host page from doing work at pointer-move frequency.
  useEffect(() => {
    if (!isEmbed || !isGeo || !manifest || !camera) return;
    const longitude = Number(camera.longitude);
    const latitude = Number(camera.latitude);
    const zoom = Number(camera.zoom);
    if (![longitude, latitude, zoom].every(Number.isFinite)) return;
    const timer = window.setTimeout(
      () => postEmbedEvent({ type: 'camera', mapId, camera: { longitude, latitude, zoom } }),
      160,
    );
    return () => window.clearTimeout(timer);
  }, [isEmbed, isGeo, manifest, camera, mapId]);

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

  const dismissSelection = () => {
    setSelection(null);
    if (isEmbed) postEmbedEvent({ type: 'selection', mapId, selection: null });
  };

  const mapCanvas = (
    <div className="map-canvas" ref={mapFrameRef} aria-label={`${manifest?.view.title ?? mapId} map`}>
      {/* Geo views: MapLibre is the root (owns camera + sizing); deck marks overlay on top. */}
      {isGeo && camera && (
        <BaseMap
          mapStyle={basemapStyle(theme)}
          {...(camera as object)}
          onMove={(e: { viewState: CameraState }) => setCamera(e.viewState)}
          onMoveEnd={(e: { viewState: CameraState }) => {
            setCamera(e.viewState);
            if (!isEmbed) {
              replaceViewerState({
                camera: {
                  longitude: Number(e.viewState.longitude),
                  latitude: Number(e.viewState.latitude),
                  zoom: Number(e.viewState.zoom),
                },
              });
            }
          }}
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
    </div>
  );

  const statusMessage = error ?? loadError;
  const status = manifest ? (
    <>
      <span>{selKeys.length} tiles</span>
      <span>{marksLoaded.toLocaleString()} cells resident</span>
    </>
  ) : (
    <span className={statusMessage ? 'status-error' : undefined}>{statusMessage ?? 'Loading map release…'}</span>
  );

  const controls = manifest ? (
    <Controls
      colorChannels={colorableChannels(manifest)}
      colorChannel={colorChannel}
      onColorChannelChange={setColorChannel}
      channels={carriedFilterableChannels(manifest)}
      options={options}
      filters={filters}
      onFilterChange={(name, value) => setFilters((current) => ({ ...current, [name]: value }))}
    />
  ) : (
    <p className="viewer-note">Controls load with the map release.</p>
  );
  const legendContent = manifest && legend ? (
    <LegendBox legend={legend} />
  ) : (
    <p className="viewer-note">Legend loads with the release.</p>
  );
  const inspection = selection ? (
    <InspectPanel selection={selection} onClose={dismissSelection} />
  ) : (
    <p className="viewer-note">Select a cell to inspect its values.</p>
  );
  const diagnostics = isPerfOn() ? (
    <PerfDashboard manifest={manifest} tilesInView={selKeys.length} cache={cacheGauge} />
  ) : null;

  if (isEmbed) {
    return (
      <EmbedShell>
        <EmbedSidebar
          side="left"
          open={controlsOpen}
          onToggle={() => setControlsOpen((open) => !open)}
          label="Map controls"
        >
          {statusMessage && <p className="status-error">{statusMessage}</p>}
          {controls}
        </EmbedSidebar>

        <EmbedStage map={mapCanvas} title={manifest?.view.title ?? mapId} status={status} />

        <EmbedSidebar
          side="right"
          open={detailsOpen}
          onToggle={() => setDetailsOpen((open) => !open)}
          label="Map details"
        >
          {legendContent}
          <div className="embed-inspection">{inspection}</div>
          {diagnostics}
        </EmbedSidebar>
      </EmbedShell>
    );
  }

  return (
    <ViewerShell controlsOpen={controlsOpen} detailsOpen={detailsOpen}>
      <ViewerHeader
        eyebrow="Colossus Maps"
        title={manifest?.view.title ?? mapId}
        release={manifest?.version}
        actions={<ThemeToggle theme={theme} onToggle={toggle} />}
      />

      <ViewerPanel
        side="left"
        open={controlsOpen}
        onToggle={() => setControlsOpen((open) => !open)}
        label="Map controls"
      >
        <PanelSection eyebrow="Map controls" title="Layers & filters">
          {statusMessage && <p className="status-error">{statusMessage}</p>}
          {controls}
        </PanelSection>
        {manifest && (
          <details className="viewer-disclosure">
            <summary>Release details</summary>
            <dl className="release-details">
              <div><dt>Cells</dt><dd>{manifest.totalPoints.toLocaleString()}</dd></div>
              <div><dt>Reduction</dt><dd>{manifest.reduction}</dd></div>
              <div><dt>Release</dt><dd>{manifest.version}</dd></div>
            </dl>
          </details>
        )}
        {manifest && <EmbedDisclosure getEmbed={getEmbed} />}
      </ViewerPanel>

      <MapWindow map={mapCanvas} status={status} />

      <ViewerPanel
        side="right"
        open={detailsOpen}
        onToggle={() => setDetailsOpen((open) => !open)}
        label="Map details"
      >
        <PanelSection eyebrow="Visible data" title="Legend">{legendContent}</PanelSection>
        <PanelSection eyebrow="Pinned mark" title="Inspection">{inspection}</PanelSection>
        {diagnostics}
      </ViewerPanel>
    </ViewerShell>
  );
}
