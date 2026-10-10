import { TILES_BASE } from '@/api/client';

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
