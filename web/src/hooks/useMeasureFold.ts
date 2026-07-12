import { useEffect, useMemo, useRef, useState } from 'react';
import type { Manifest } from '../lib/manifest';
import { isGroupRegime } from '../lib/channels';
import { buildFoldContext, type CompanionData, foldTile, type MeasureExpr, parseMeasure } from '../lib/measures';
import { companionGrain } from '../lib/tileData';
import { tileLoader } from '../lib/tileLoader';
import type { RenderedTile } from './useTiles';

export type FoldedColumns = Record<string, Float32Array | Uint16Array>;

/** Folded measure columns for the on-screen tiles, tagged with the context that produced them. The tag
 *  is what keys derived GPU buffers — never the *current* selection, which may be a fold ahead of the
 *  arrays (the poisoned-cache bug: buffers cached under a context they don't represent). */
export interface FoldResult {
  byTile: Map<string, FoldedColumns>;
  contextSig: string;
}

// Companions cached per (version, tile); fold results per (version, tile, context). Bounded below.
const FOLD_CACHE_CAP = 256;
type CompanionEntry = CompanionData | 'missing';

/** Per-tile folded measure columns under the active perFact context, or null when there is no context
 *  (render the baked default-context values — zero extra work). Companions are fetched and decoded on
 *  the worker pool (typed columns, transferred), cached per tile, and folded once per (tile, context) —
 *  a pan or zoom re-uses every fold it has already done; only new tiles (or a new context) fold, and the
 *  fold itself is typed-array arithmetic joined by the bake-written `mki`. (GROUP-MEASURES §7–8.) */
export function useMeasureFold(
  manifest: Manifest | null,
  rendered: RenderedTile[],
  context: Record<string, string>,
): FoldResult | null {
  const [folded, setFolded] = useState<FoldResult | null>(null);
  const companions = useRef(new Map<string, CompanionEntry>());
  const folds = useRef(new Map<string, FoldedColumns>());

  const measures = useMemo<{ name: string; ast: MeasureExpr }[]>(
    () => (manifest?.view.measures ?? []).map((m) => ({ name: m.name, ast: parseMeasure(m.expr) })),
    [manifest],
  );

  // argmax measures colour over their dimension's baked domain — the fold codes into it.
  const domains = useMemo<Record<string, string[]>>(() => {
    const out: Record<string, string[]> = {};
    for (const m of measures) {
      const ast = m.ast;
      if ((ast.kind === 'argmax' || ast.kind === 'argmin') && manifest?.channelDomains) {
        const d = manifest.channelDomains[ast.dimension]?.values;
        if (d) out[ast.dimension] = d;
      }
    }
    return out;
  }, [measures, manifest]);

  const active = !!manifest && isGroupRegime(manifest.view) && !!manifest.companionTiles && Object.keys(context).length > 0;
  const contextSig = JSON.stringify(context);
  const renderSig = rendered.map((t) => t.key).join(',');

  useEffect(() => {
    if (!manifest || !active) {
      setFolded(null);
      return;
    }
    let alive = true;
    const ctx = buildFoldContext(manifest.view, context);
    const version = manifest.version;
    const grain = companionGrain(manifest);
    const ckey = (key: string) => `${version}|${key}`;
    (async () => {
      // Fetch + decode the missing companions in parallel on the worker pool; a tile without one
      // (older bake / missing file) keeps its baked colours and is never re-requested.
      await Promise.all(
        rendered
          .filter((t) => !companions.current.has(ckey(t.key)))
          .map(async (t) => {
            try {
              const c = await tileLoader.loadCompanion(manifest.view.id, version, t.key, grain);
              companions.current.set(ckey(t.key), c);
            } catch {
              companions.current.set(ckey(t.key), 'missing');
            }
          }),
      );
      if (!alive) return;

      const byTile = new Map<string, FoldedColumns>();
      for (const t of rendered) {
        const fkey = `${version}|${t.key}|${contextSig}`;
        let cols = folds.current.get(fkey);
        if (!cols) {
          const comp = companions.current.get(ckey(t.key));
          if (!comp || comp === 'missing') continue;
          cols = foldTile(comp, measures, ctx, t.data.count, domains);
          folds.current.set(fkey, cols);
        }
        byTile.set(t.key, cols);
      }
      setFolded({ byTile, contextSig });

      // Evict caches for tiles no longer on screen (bounded like the tile cache), then cap the fold
      // cache so a long scrub can't hold every context it visited (oldest-first, insertion order).
      const live = new Set(rendered.map((t) => ckey(t.key)));
      for (const k of companions.current.keys()) if (!live.has(k)) companions.current.delete(k);
      for (const k of folds.current.keys()) if (!live.has(k.slice(0, k.lastIndexOf('|')))) folds.current.delete(k);
      let over = folds.current.size - FOLD_CACHE_CAP;
      if (over > 0) for (const k of folds.current.keys()) { folds.current.delete(k); if (--over <= 0) break; }
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manifest, active, contextSig, renderSig, measures, domains]);

  return active ? folded : null;
}
