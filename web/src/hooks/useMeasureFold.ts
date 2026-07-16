import { useEffect, useMemo, useRef, useState } from 'react';
import type { Manifest } from '../lib/manifest';
import { packBlockUrl } from '../lib/manifest';
import { isGroupRegime } from '../lib/channels';
import { buildFoldContext, foldTile, type MeasureExpr, parseMeasure } from '../lib/measures';
import { companionGrain, type Companion, type CompanionFetch, packBlock } from '../lib/tileData';
import { foldSlab, isSlab, slabPlanesForMeasures } from '../lib/slab';
import { tileLoader } from '../lib/tileLoader';
import type { RenderedTile } from './useTiles';

export type FoldedColumns = Record<string, Float32Array | Uint16Array>;

/** Folded measure columns for the on-screen tiles, tagged with the context that produced them. The tag
 *  is what keys derived GPU buffers — never the *current* selection, which may be a fold ahead of the
 *  arrays (the poisoned-cache bug: buffers cached under a context they don't represent). `missing` names
 *  the tiles that can never fold (no companion — older bake / missing file): they keep their baked
 *  colours, whereas a tile absent from both maps is merely pending and must not draw baked values into
 *  a filtered frame. */
export interface FoldResult {
  byTile: Map<string, FoldedColumns>;
  missing: Set<string>;
  contextSig: string;
}

/** Whether a perFact context drives a fold for this manifest — the render gate shares it, so "hold a
 *  tile until its fold lands" can never disagree with whether a fold is coming at all. */
export const foldActive = (manifest: Manifest | null, context: Record<string, string>): boolean =>
  !!manifest && isGroupRegime(manifest.view) && !!manifest.companionTiles && Object.keys(context).length > 0;

// Companions cached per (version, tile); fold results per (version, tile, context). Bounded below.
const FOLD_CACHE_CAP = 256;
type CompanionEntry = Companion | 'missing';

/** Per-tile folded measure columns under the active perFact context, or null when there is no context
 *  (render the baked default-context values — zero extra work). Companions are fetched and decoded on
 *  the worker pool (typed columns, transferred), cached per tile, and folded once per (tile, context) —
 *  a pan or zoom re-uses every fold it has already done; only new tiles (or a new context) fold, and the
 *  fold itself is typed-array arithmetic joined by the bake-written `mki`. */
export function useMeasureFold(
  manifest: Manifest | null,
  rendered: RenderedTile[],
  context: Record<string, string>,
): FoldResult | null {
  const [folded, setFolded] = useState<FoldResult | null>(null);
  const [retry, setRetry] = useState(0);
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

  const active = foldActive(manifest, context);
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
    // Slab bakes fetch each tile's active planes (all folded measures) out of the pack (R1/R5); row-form
    // bakes range the tile's block or fetch the per-file companion.
    const slabActive = isSlab(manifest);
    const want = slabActive ? slabPlanesForMeasures(manifest, measures.map((m) => m.name)) : [];
    const companionFetch = (key: string): CompanionFetch | null => {
      if (slabActive) {
        const pack = manifest.companionPack;
        const dir = pack?.planeEntries?.[key];
        if (!pack || !dir || !manifest.companionSlab) return null;
        return { kind: 'slab', baseUrl: packBlockUrl(manifest.view.id, version, pack.file, key), codec: pack.codec, slab: manifest.companionSlab, dir, want };
      }
      const pack = packBlock(manifest.companionPack, manifest.view.id, version, key);
      return { kind: 'row', viewId: manifest.view.id, version, key, grain, pack };
    };
    (async () => {
      // Fetch + decode the missing companions in parallel on the worker pool. Only a definitive miss
      // (404, or a companion the fold can't use — older bake) is cached as 'missing': that tile keeps
      // its baked colours and is never re-requested. A transient failure (network hiccup, server
      // restart) stays un-entered and schedules a retry — caching it would pin the tile to baked or
      // stale-context colours for the rest of the session.
      let transient = false;
      await Promise.all(
        rendered
          .filter((t) => !companions.current.has(ckey(t.key)))
          .map(async (t) => {
            const spec = companionFetch(t.key);
            if (!spec) {
              companions.current.set(ckey(t.key), 'missing');
              return;
            }
            try {
              const c = await tileLoader.loadCompanion(spec);
              companions.current.set(ckey(t.key), c);
            } catch (e) {
              const msg = e instanceof Error ? e.message : String(e);
              if (/ 404 /.test(msg) || msg.includes('no mki column')) companions.current.set(ckey(t.key), 'missing');
              else transient = true;
            }
          }),
      );
      if (!alive) return;
      if (transient) setTimeout(() => alive && setRetry((r) => r + 1), 1500);

      const byTile = new Map<string, FoldedColumns>();
      const missing = new Set<string>();
      for (const t of rendered) {
        const fkey = `${version}|${t.key}|${contextSig}`;
        let cols = folds.current.get(fkey);
        if (!cols) {
          const comp = companions.current.get(ckey(t.key));
          if (!comp || comp === 'missing') {
            if (comp === 'missing') missing.add(t.key);
            continue;
          }
          cols =
            comp.kind === 'slab'
              ? foldSlab(comp.data, measures, ctx, t.data.count, domains)
              : foldTile(comp.data, measures, ctx, t.data.count, domains);
          folds.current.set(fkey, cols);
        }
        byTile.set(t.key, cols);
      }
      setFolded({ byTile, missing, contextSig });

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
  }, [manifest, active, contextSig, renderSig, measures, domains, retry]);

  return active ? folded : null;
}
