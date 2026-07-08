import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import DeckGL from '@deck.gl/react';
import {
  COORDINATE_SYSTEM,
  MapView,
  OrthographicView,
  WebMercatorViewport,
  type Viewport as DeckViewport,
} from '@deck.gl/core';
import { ScatterplotLayer, SolidPolygonLayer } from '@deck.gl/layers';
import { MapboxOverlay, type MapboxOverlayProps } from '@deck.gl/mapbox';
// Aliased: a bare `Map` import would shadow the built-in Map constructor this file uses for its cache.
import { Map as BaseMap, useControl } from 'react-map-gl/maplibre';
import 'maplibre-gl/dist/maplibre-gl.css';
import { basemapStyle } from './lib/basemap';
import { rampColor, valuesToColors } from './lib/colors';
import { loadManifest, type Manifest } from './lib/manifest';
import {
  ALL,
  buildWhere,
  coverTiles,
  discoverOptions,
  filterableChannels,
  loadTile,
  measureChannel,
  measureChannels,
  measureRange,
  pruneCache,
  selectTiles,
  type TileData,
  type ViewBounds,
} from './lib/tiles';

const VIEW_IDS = ['ookla-fixed'];
const RETRY_MS = 2000;
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

// A tile's geometry buffers are loaded once; only its color changes when you switch measure or the ramp
// rescales. So we memoize the deck binary `data` object per (tile, measure, range): identity stays stable
// across camera moves — deck skips re-tessellation, the pan/zoom freeze is gone — and recoloring is a
// client-side scan of already-resident values, never a re-fetch.
const tileDeckCache = new WeakMap<TileData, Map<string, object>>();
function tileDeckData(d: TileData, measure: string, range: [number, number]): object {
  let byKey = tileDeckCache.get(d);
  if (!byKey) {
    byKey = new Map();
    tileDeckCache.set(d, byKey);
  }
  const cacheKey = `${measure}|${range[0]}|${range[1]}`;
  let data = byKey.get(cacheKey);
  if (!data) {
    const vals = d.values[measure] ?? new Float32Array(d.count);
    if (d.polyPositions) {
      // Per-vertex color: each cell's ramp color repeated across its ring's vertices.
      const span = range[1] - range[0] || 1;
      const si = d.polyStartIndices!;
      const colors = new Uint8Array(d.vertexCount! * 3);
      for (let p = 0; p < d.count; p++) {
        const [r, g, b] = rampColor((vals[p] - range[0]) / span);
        for (let v = si[p]; v < si[p + 1]; v++) {
          colors[v * 3] = r;
          colors[v * 3 + 1] = g;
          colors[v * 3 + 2] = b;
        }
      }
      const attributes: Record<string, object> = {
        getPolygon: { value: d.polyPositions, size: 2 },
        getFillColor: { value: colors, size: 3, normalized: true },
      };
      // Bake-time tessellation: with an external indices buffer deck skips its per-polygon earcut —
      // the synchronous main-thread block that made stutter scale with cell count. Loading a tile
      // becomes a pure GPU upload.
      if (d.polyTriangles) attributes.indices = { value: d.polyTriangles, size: 1 };
      data = { length: d.count, startIndices: si, attributes };
    } else {
      data = {
        length: d.count,
        attributes: {
          getPosition: { value: d.positions!, size: 2 },
          getFillColor: { value: valuesToColors(vals, range[0], range[1]), size: 3, normalized: true },
        },
      };
    }
    byKey.set(cacheKey, data);
  }
  return data;
}

// deck.gl layers as a MapLibre control, overlaid on the base map. Geo views mount MapLibre as the
// root so the map owns camera + canvas sizing; the marks ride on top through this overlay.
function DeckOverlay(props: MapboxOverlayProps) {
  const overlay = useControl(() => new MapboxOverlay(props));
  overlay.setProps(props);
  return null;
}

function initialViewState(m: Manifest) {
  const { root } = m;
  const cx = (root.minX + root.maxX) / 2;
  const cy = (root.minY + root.maxY) / 2;
  if (m.view.viewport === 'geo') {
    const spanX = root.maxX - root.minX || 1;
    return {
      longitude: clamp(cx, -180, 180),
      latitude: clamp(cy, -85, 85),
      zoom: clamp(Math.log2(360 / spanX), 0, 14),
      pitch: 0,
      bearing: 0,
    };
  }
  return { target: [cx, cy, 0] as [number, number, number], zoom: Math.log2((window.innerWidth * 0.9) / (root.maxX - root.minX)) };
}

function boundsFromViewport(vp: DeckViewport): ViewBounds {
  const [ax, ay] = vp.unproject([0, 0]);
  const [bx, by] = vp.unproject([vp.width, vp.height]);
  return {
    minX: Math.min(ax, bx),
    maxX: Math.max(ax, bx),
    minY: Math.min(ay, by),
    maxY: Math.max(ay, by),
    widthPx: vp.width,
  };
}

export default function App() {
  const [viewId, setViewId] = useState(VIEW_IDS[0]);
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [viewState, setViewState] = useState<Record<string, unknown> | null>(null);
  const [size, setSize] = useState({ width: window.innerWidth, height: window.innerHeight });
  const [range, setRange] = useState<[number, number]>([0, 100]);
  const [options, setOptions] = useState<Record<string, string[]>>({});
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [measure, setMeasure] = useState<string>('');
  const [selKeys, setSelKeys] = useState<string[]>([]);

  const cache = useRef(new Map<string, TileData>());
  const loading = useRef(new Set<string>());
  const failedAt = useRef(new Map<string, number>());
  // Bumped on every tile load/failure: the render side keys off this, not cache size — at the cache
  // cap each load also evicts one, leaving the size constant and (before) the redraw skipped.
  const [epoch, bump] = useReducer((x) => x + 1, 0);

  useEffect(() => {
    const onResize = () => setSize({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  // Load the manifest, then sample the store for the color range + filter options. A polygon view
  // defaults to one clean slice (first value of each dimension); a point view starts unfiltered.
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setError(null);
    loadManifest(viewId)
      .then(async (m) => {
        if (!alive) return;
        cache.current.clear();
        loading.current.clear();
        failedAt.current.clear();
        setManifest(m);
        setViewState(initialViewState(m));
        setSelKeys([]);
        setMeasure(measureChannel(m.view));
        const o = await discoverOptions(m.view, m.version);
        if (!alive) return;
        setOptions(o);
        const defaults: Record<string, string> = {};
        for (const ch of filterableChannels(m.view)) {
          const opts = o[ch.name] ?? [];
          const pick = ch.role === 'temporal' ? opts[opts.length - 1] : opts[0];
          defaults[ch.name] = m.view.mark === 'polygon' ? pick ?? ALL : ALL;
        }
        setFilters(defaults);
      })
      .catch((e) => {
        if (alive) setError(`${viewId}: ${e instanceof Error ? e.message : 'not baked yet'}`);
      });
    return () => {
      alive = false;
    };
  }, [viewId]);

  // The color ramp rescales to whichever measure is active — re-sampled from the root tile.
  useEffect(() => {
    if (!manifest || !measure) return;
    let alive = true;
    measureRange(manifest.view, manifest.version, measure).then((r) => alive && setRange(r));
    return () => {
      alive = false;
    };
  }, [manifest, measure]);

  const filterSql = useMemo(() => (manifest ? buildWhere(manifest.view, filters) : ''), [filters, manifest]);

  const view = useMemo(
    () =>
      manifest?.view.viewport === 'geo'
        ? new MapView({ id: 'main', controller: true })
        : new OrthographicView({ id: 'main', controller: true, flipY: false }),
    [manifest],
  );

  // Recompute the visible tile set whenever camera / manifest / size / filter changes; fetch misses.
  // Cache keys fold in the filter so switching slices re-queries rather than showing stale rows.
  useEffect(() => {
    if (!manifest || !viewState) return;
    const vp =
      manifest.view.viewport === 'geo'
        ? new WebMercatorViewport({ ...viewState, width: size.width, height: size.height })
        : new OrthographicView({ flipY: false }).makeViewport({
            width: size.width,
            height: size.height,
            viewState: viewState as never,
          });
    if (!vp) return;

    const keys = selectTiles(manifest, boundsFromViewport(vp));
    // Keep the previous array identity when the selection didn't change: the layers memo then skips
    // entirely during a pan/zoom within the same tile set, instead of rebuilding layer instances and
    // re-diffing them every camera frame.
    setSelKeys((prev) => (prev.length === keys.length && prev.every((v, i) => v === keys[i]) ? prev : keys));

    // Cache key folds in only the filter — a tile carries every measure, so switching measure recolors
    // from cache with no re-fetch. Measure/range never trigger a load.
    for (const key of keys) {
      const ck = `${filterSql}|${key}`;
      if (cache.current.has(ck) || loading.current.has(ck)) continue;
      if (Date.now() - (failedAt.current.get(ck) ?? -Infinity) < RETRY_MS) continue;
      loading.current.add(ck);
      loadTile(manifest.view, manifest.version, key, filterSql)
        .then((data) => {
          cache.current.set(ck, data);
          loading.current.delete(ck);
          failedAt.current.delete(ck);
          // Tiles standing in as cover are on screen too — evicting one mid-transition would flicker.
          const cover = coverTiles(keys, (k) => cache.current.has(`${filterSql}|${k}`));
          pruneCache(cache.current, new Set([...keys, ...cover].map((k) => `${filterSql}|${k}`)));
          bump();
        })
        .catch(() => {
          loading.current.delete(ck);
          failedAt.current.set(ck, Date.now());
          setTimeout(bump, RETRY_MS);
        });
    }
  }, [manifest, viewState, size, filterSql, epoch]);

  const isGeo = manifest?.view.viewport === 'geo';
  const isPolygon = manifest?.view.mark === 'polygon';

  // Draw the cover, not the raw selection: loaded stand-ins hold the screen until a quad's tiles all
  // arrive, then the swap lands on identical pixels (see coverTiles).
  const rendered = useMemo(
    () => coverTiles(selKeys, (k) => cache.current.has(`${filterSql}|${k}`)),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [selKeys, filterSql, epoch],
  );

  const layers = useMemo(() => {
    if (!manifest) return [];
    const coordinateSystem = isGeo ? COORDINATE_SYSTEM.LNGLAT : COORDINATE_SYSTEM.CARTESIAN;
    return rendered
      .map((k) => ({ k, d: cache.current.get(`${filterSql}|${k}`) }))
      .filter((e): e is { k: string; d: TileData } => !!e.d)
      .map(({ k, d }) => {
        // Key by tile ONLY — never by measure/filter. A stable id lets deck match the existing layer
        // and swap attribute buffers in place; folding measure into the id destroyed and rebuilt every
        // on-screen layer per picker switch, re-running geometry setup for ~1M cells in one frame.
        const id = `t-${viewId}-${manifest.version}-${k}`;
        if (isPolygon) {
          return new SolidPolygonLayer({
            id,
            // Binary layout: flat vertices + per-polygon start offsets + per-vertex colors. No object
            // accessors, so nothing runs per-cell on the main thread — it uploads straight to the GPU.
            data: tileDeckData(d, measure, range) as never,
            _normalize: false, // rings are already simple + consistent from the bake
            positionFormat: 'XY',
            coordinateSystem,
            opacity: 0.85,
          });
        }
        return new ScatterplotLayer({
          id,
          data: tileDeckData(d, measure, range) as never,
          coordinateSystem,
          radiusUnits: 'pixels',
          getRadius: 1.6,
          radiusMinPixels: 1,
          radiusMaxPixels: 3,
          stroked: false,
          opacity: 0.85,
        });
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rendered, viewId, manifest, isGeo, isPolygon, measure, range, filterSql]);

  const marksLoaded = rendered.reduce((s, k) => s + (cache.current.get(`${filterSql}|${k}`)?.count ?? 0), 0);
  const channels = manifest ? filterableChannels(manifest.view) : [];
  const measures = manifest ? measureChannels(manifest.view) : [];

  // Are all the tiles currently on screen leaves (every real cell), or are some coarse aggregates?
  // That's the difference between full fidelity and a "zoom in to resolve" preview (RULES R2).
  const tileIndex = useMemo(
    () => new Map((manifest?.tiles ?? []).map((t) => [`${t.z}/${t.x}/${t.y}`, t])),
    [manifest],
  );
  const atFullFidelity =
    rendered.length > 0 && selKeys.every((k) => tileIndex.get(k)?.isLeaf && cache.current.has(`${filterSql}|${k}`));

  return (
    <div style={{ position: 'absolute', inset: 0, background: '#0a0a0a' }}>
      {/* Geo views: MapLibre is the root (owns camera + sizing); deck marks overlay on top. */}
      {isGeo && viewState && (
        <BaseMap
          mapStyle={basemapStyle}
          {...(viewState as object)}
          onMove={(e: { viewState: Record<string, unknown> }) => setViewState(e.viewState)}
          onLoad={(e: { target: { resize: () => void } }) => e.target.resize()}
          style={{ position: 'absolute', inset: 0 }}
        >
          <DeckOverlay layers={layers} />
        </BaseMap>
      )}

      {/* Non-geo views: deck.gl is the root with an orthographic camera, no base map. */}
      {!isGeo && view && viewState && (
        <DeckGL
          views={view}
          viewState={viewState as never}
          controller
          onViewStateChange={(e: { viewState: Record<string, unknown> }) => setViewState(e.viewState)}
          layers={layers}
        />
      )}

      <div style={hud}>
        <div style={{ fontWeight: 700, marginBottom: 6 }}>Colossus</div>
        <div style={{ display: 'flex', gap: 6, marginBottom: 8 }}>
          {VIEW_IDS.map((id) => (
            <button key={id} onClick={() => setViewId(id)} style={id === viewId ? btnOn : btn}>
              {id}
            </button>
          ))}
        </div>

        {error && <div style={{ color: '#f77', marginBottom: 8, maxWidth: 220 }}>{error}</div>}

        {measures.length > 0 && (
          <div style={{ marginBottom: 6 }}>
            <label style={{ opacity: 0.7, display: 'block', fontSize: 11 }}>color by</label>
            <select value={measure} onChange={(e) => setMeasure(e.target.value)} style={select}>
              {measures.map((m) => (
                <option key={m.name} value={m.name}>
                  {m.name}
                </option>
              ))}
            </select>
          </div>
        )}

        {channels.map((ch) => {
          const opts = options[ch.name] ?? [];
          return (
            <div key={ch.name} style={{ marginBottom: 6 }}>
              <label style={{ opacity: 0.7, display: 'block', fontSize: 11 }}>{ch.name}</label>
              {ch.role === 'temporal' ? (
                <input
                  type="date"
                  value={filters[ch.name] && filters[ch.name] !== ALL ? filters[ch.name] : ''}
                  min={opts[0]}
                  max={opts[opts.length - 1]}
                  onChange={(e) => setFilters((f) => ({ ...f, [ch.name]: e.target.value || ALL }))}
                  style={select}
                />
              ) : (
                <select
                  value={filters[ch.name] ?? ALL}
                  onChange={(e) => setFilters((f) => ({ ...f, [ch.name]: e.target.value }))}
                  style={select}
                >
                  <option value={ALL}>{ALL}</option>
                  {opts.map((v) => (
                    <option key={v} value={v}>
                      {v}
                    </option>
                  ))}
                </select>
              )}
            </div>
          );
        })}

        {manifest && (
          <div style={{ opacity: 0.85, lineHeight: 1.6, marginTop: 8 }}>
            <div>cells total: {manifest.totalPoints.toLocaleString()}</div>
            <div>
              in view: {selKeys.length} tiles · {marksLoaded.toLocaleString()} cells
            </div>
            <div style={{ marginTop: 4, fontWeight: 600, color: atFullFidelity ? '#7ee787' : '#e3b341' }}>
              {atFullFidelity ? '● full fidelity — every cell' : '◐ aggregated — zoom in to resolve'}
            </div>
            <div style={{ opacity: 0.5, fontSize: 11, marginTop: 4 }}>
              {manifest.reduction} · {manifest.version}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

const hud: React.CSSProperties = {
  position: 'absolute',
  top: 12,
  left: 12,
  padding: '10px 12px',
  background: 'rgba(20,20,20,0.82)',
  color: '#eee',
  font: '12px system-ui, sans-serif',
  borderRadius: 8,
  border: '1px solid #333',
  userSelect: 'none',
  minWidth: 160,
};
const btn: React.CSSProperties = {
  padding: '4px 8px',
  background: '#222',
  color: '#ccc',
  border: '1px solid #444',
  borderRadius: 6,
  cursor: 'pointer',
};
const btnOn: React.CSSProperties = { ...btn, background: '#2d6', color: '#022', border: '1px solid #2d6', fontWeight: 700 };
const select: React.CSSProperties = {
  width: '100%',
  padding: '3px 4px',
  background: '#1a1a1a',
  color: '#ddd',
  border: '1px solid #444',
  borderRadius: 4,
};
