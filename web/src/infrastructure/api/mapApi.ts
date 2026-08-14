import type { Manifest } from '@domain/map/manifest';
import { API_BASE, TILES_BASE } from '@shared/api/config';

export { API_BASE };

export function tileUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.arrow`;
}

export function factsUrl(viewId: string, version: string, z: number, x: number, y: number): string {
  return `${TILES_BASE}/${viewId}/${version}/${z}/${x}/${y}.facts.arrow`;
}

/** Static servers ignore the query; it makes the service-worker cache key specific to one tile block. */
export function packBlockUrl(viewId: string, version: string, file: string, key: string): string {
  return `${TILES_BASE}/${viewId}/${version}/${file}?tile=${key}`;
}

export function packDictUrl(
  viewId: string,
  version: string,
  pack: { dict?: string } | undefined,
): string | undefined {
  return pack?.dict ? `${TILES_BASE}/${viewId}/${version}/${pack.dict}` : undefined;
}

/** Resolve the mutable pointer once, then load the immutable release manifest. */
export async function loadManifest(viewId: string): Promise<Manifest> {
  const pointerResponse = await fetch(`${TILES_BASE}/${viewId}/latest.json`, { cache: 'no-cache' });
  if (!pointerResponse.ok) throw new Error(`latest manifest ${pointerResponse.status}`);
  const pointer: { version: string } = await pointerResponse.json();

  const manifestResponse = await fetch(`${TILES_BASE}/${viewId}/${pointer.version}/manifest.json`);
  if (!manifestResponse.ok) throw new Error(`release manifest ${manifestResponse.status}`);
  return manifestResponse.json();
}
