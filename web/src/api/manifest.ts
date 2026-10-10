import type { Manifest } from '@/types/manifest';
import { TILES_BASE } from './client';

/** Resolve the mutable pointer once, then load the immutable release manifest. */
export async function loadManifest(viewId: string): Promise<Manifest> {
  const pointerResponse = await fetch(`${TILES_BASE}/${viewId}/latest.json`, { cache: 'no-cache' });
  if (!pointerResponse.ok) throw new Error(`latest manifest ${pointerResponse.status}`);
  const pointer: { version: string } = await pointerResponse.json();

  const manifestResponse = await fetch(`${TILES_BASE}/${viewId}/${pointer.version}/manifest.json`);
  if (!manifestResponse.ok) throw new Error(`release manifest ${manifestResponse.status}`);
  return manifestResponse.json();
}
