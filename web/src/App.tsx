import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import DeckGL from '@deck.gl/react';
import {
  COORDINATE_SYSTEM,
  MapView,
  OrthographicView,
  WebMercatorViewport,
  type Viewport as DeckViewport,
} from '@deck.gl/core';
import { ScatterplotLayer } from '@deck.gl/layers';
import { loadManifest, type Manifest } from './lib/manifest';
import { loadTile, pruneCache, selectTiles, type TileData, type ViewBounds } from './lib/tiles';

const VIEW_IDS = ['geo-points', 'xy-scatter'];
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

function initialViewState(m: Manifest, width: number) {
  const { root } = m;
  const cx = (root.minX + root.maxX) / 2;
  const cy = (root.minY + root.maxY) / 2;
  if (m.view.viewport === 'Geo') {
    return { longitude: clamp(cx, -180, 180), latitude: clamp(cy, -85, 85), zoom: 1.3, pitch: 0, bearing: 0 };
  }
  return { target: [cx, cy, 0] as [number, number, number], zoom: Math.log2((width * 0.9) / (root.maxX - root.minX)) };
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
  const [selKeys, setSelKeys] = useState<string[]>([]);

  const cache = useRef(new Map<string, TileData>());
  const loading = useRef(new Set<string>());
  const [, tick] = useReducer((x) => x + 1, 0);

  // Track window size.
  useEffect(() => {
    const onResize = () => setSize({ width: window.innerWidth, height: window.innerHeight });
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  }, []);

  // Load the manifest for the selected view and reset camera + caches.
  useEffect(() => {
    let alive = true;
    loadManifest(viewId).then((m) => {
      if (!alive) return;
      cache.current.clear();
      loading.current.clear();
      setManifest(m);
      setViewState(initialViewState(m, window.innerWidth));
      setSelKeys([]);
    });
    return () => {
      alive = false;
    };
  }, [viewId]);

  const view = useMemo(
    () =>
      manifest?.view.viewport === 'Geo'
        ? new MapView({ id: 'main', controller: true })
        : new OrthographicView({ id: 'main', controller: true, flipY: false }),
    [manifest],
  );

  // Recompute the visible tile set whenever the camera, manifest, or size changes; fetch what's missing.
  useEffect(() => {
    if (!manifest || !viewState) return;
    const vp =
      manifest.view.viewport === 'Geo'
        ? new WebMercatorViewport({ ...viewState, width: size.width, height: size.height })
        : new OrthographicView({ flipY: false }).makeViewport({
            width: size.width,
            height: size.height,
            viewState: viewState as never,
          });

    if (!vp) return;
    const keys = selectTiles(manifest, boundsFromViewport(vp));
    setSelKeys(keys);

    const active = new Set(keys);
    for (const key of keys) {
      if (cache.current.has(key) || loading.current.has(key)) continue;
      loading.current.add(key);
      loadTile(viewId, manifest.version, key)
        .then((data) => {
          cache.current.set(key, data);
          loading.current.delete(key);
          pruneCache(cache.current, active);
          tick();
        })
        .catch(() => loading.current.delete(key));
    }
  }, [manifest, viewState, size, viewId]);

  const isGeo = manifest?.view.viewport === 'Geo';
  const layers = useMemo(
    () =>
      selKeys
        .filter((k) => cache.current.has(k))
        .map((k) => {
          const d = cache.current.get(k)!;
          return new ScatterplotLayer({
            id: `t-${viewId}-${manifest?.version}-${k}`,
            data: {
              length: d.length,
              attributes: {
                getPosition: { value: d.positions, size: 2 },
                getFillColor: { value: d.colors, size: 3 },
              },
            },
            coordinateSystem: isGeo ? COORDINATE_SYSTEM.LNGLAT : COORDINATE_SYSTEM.CARTESIAN,
            radiusUnits: 'pixels',
            getRadius: 1.6,
            radiusMinPixels: 1,
            radiusMaxPixels: 3,
            stroked: false,
            opacity: 0.85,
            parameters: { depthTest: false },
          });
        }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [selKeys, viewId, manifest?.version, isGeo, cache.current.size],
  );

  const pointsLoaded = selKeys.reduce((s, k) => s + (cache.current.get(k)?.length ?? 0), 0);

  return (
    <div style={{ position: 'absolute', inset: 0 }}>
      {view && viewState && (
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
        {manifest && (
          <div style={{ opacity: 0.85, lineHeight: 1.6 }}>
            <div>viewport: {manifest.view.viewport}</div>
            <div>total: {manifest.totalPoints.toLocaleString()} pts</div>
            <div>tiles visible: {selKeys.length}</div>
            <div>points loaded: {pointsLoaded.toLocaleString()}</div>
            <div style={{ opacity: 0.5, fontSize: 11 }}>{manifest.version}</div>
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
