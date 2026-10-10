import type { Bbox } from '@/types/viewConfig';

/** The data-space rectangle the current camera sees — what tile selection culls against. */
export interface ViewBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
  widthPx: number;
}

export const tileKey = (z: number, x: number, y: number) => `${z}/${x}/${y}`;

export function tileRect(root: Bbox, z: number, x: number, y: number) {
  const n = 2 ** z;
  const cw = (root.maxX - root.minX) / n;
  const ch = (root.maxY - root.minY) / n;
  const xMin = root.minX + x * cw;
  const yMin = root.minY + y * ch;
  return { xMin, yMin, xMax: xMin + cw, yMax: yMin + ch, cw };
}

/** Point → tile index at zoom z — the forward map and mirror of the C# tiling authority
 *  (TileMath.PointToTile / TileSql). Pinned to it by tileGrid.test.ts against the shared fixture. */
export function pointToTile(root: Bbox, z: number, px: number, py: number): [number, number] {
  const n = 2 ** z;
  const clamp = (v: number) => Math.max(0, Math.min(v, n - 1));
  const x = Math.floor(((px - root.minX) / (root.maxX - root.minX)) * n);
  const y = Math.floor(((py - root.minY) / (root.maxY - root.minY)) * n);
  return [clamp(x), clamp(y)];
}
