import { OrthographicView, WebMercatorViewport, type Viewport as DeckViewport } from '@deck.gl/core';
import type { Manifest } from './manifest';
import type { ViewBounds } from './tiling';

/** deck/MapLibre camera state — geo (lng/lat/zoom) or orthographic (target/zoom), per the view. */
export type CameraState = Record<string, unknown>;

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

export function initialViewState(m: Manifest): CameraState {
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
