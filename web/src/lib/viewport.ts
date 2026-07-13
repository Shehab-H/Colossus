import { OrthographicView, WebMercatorViewport, type Viewport as DeckViewport } from '@deck.gl/core';
import type { Manifest } from './manifest';
import type { ViewBounds } from './tiling';

/** deck/MapLibre camera state — geo (lng/lat/zoom) or orthographic (target/zoom), per the view. */
export type CameraState = Record<string, unknown>;

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

/** Default camera that frames the view's data. Geo views fit the data bounds to the actual viewport
 *  (fitBounds accounts for size + aspect + padding), so a regional dataset fills the screen instead
 *  of sitting in a small box on a world map. `size` defaults to the window when not supplied. */
export function initialViewState(m: Manifest, size?: { width: number; height: number }): CameraState {
  const { root } = m;
  const cx = (root.minX + root.maxX) / 2;
  const cy = (root.minY + root.maxY) / 2;
  if (m.view.viewport === 'geo') {
    const width = size?.width ?? window.innerWidth;
    const height = size?.height ?? window.innerHeight;
    const spanX = root.maxX - root.minX;
    const spanY = root.maxY - root.minY;
    // fitBounds needs a real extent and finite size; fall back to the longitude-span estimate otherwise.
    if (spanX > 0 && spanY > 0 && width > 0 && height > 0) {
      const { longitude, latitude, zoom } = new WebMercatorViewport({ width, height }).fitBounds(
        [
          [root.minX, root.minY],
          [root.maxX, root.maxY],
        ],
        { padding: Math.min(48, Math.floor(Math.min(width, height) / 8)) },
      );
      return { longitude, latitude, zoom: clamp(zoom, 0, 14), pitch: 0, bearing: 0 };
    }
    return {
      longitude: clamp(cx, -180, 180),
      latitude: clamp(cy, -85, 85),
      zoom: clamp(Math.log2(360 / (spanX || 1)), 0, 14),
      pitch: 0,
      bearing: 0,
    };
  }
  return { target: [cx, cy, 0] as [number, number, number], zoom: Math.log2((window.innerWidth * 0.9) / (root.maxX - root.minX)) };
}

/** The deck viewport for the current camera — the thing tile selection culls against. */
export function viewportFor(m: Manifest, camera: CameraState, size: { width: number; height: number }): DeckViewport | null {
  return m.view.viewport === 'geo'
    ? new WebMercatorViewport({ ...camera, width: size.width, height: size.height })
    : new OrthographicView({ flipY: false }).makeViewport({
        width: size.width,
        height: size.height,
        viewState: camera as never,
      });
}

export function boundsFromViewport(vp: DeckViewport): ViewBounds {
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
