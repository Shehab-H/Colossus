import type { Manifest } from '@/types/manifest';
import { foldRouteOverride } from '../engine';

/** What the bake priced for this view, and whether a URL flag is overriding it. Informational only: the
 *  clocks report what actually ran, which is exactly the thing that can disagree with this label. */
export function routeLabel(m: Manifest | null): string {
  if (!m) return '—';
  const baked = m.foldRoute?.execution ?? 'client';
  const forced = foldRouteOverride();
  return forced && forced !== baked ? `${forced} · forced, baked ${baked}` : baked;
}
