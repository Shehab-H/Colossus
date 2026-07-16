import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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

/** The map fold plus an imperative per-tile inspect fold (foldInspect): the map colours from just the
 *  active measure's planes (R1/R5 plane split), so a tooltip — which shows every inspect channel — fetches
 *  the remaining planes for the one clicked tile on demand. */
export interface MeasureFold {
  folded: FoldResult | null;
  foldInspect: (key: string) => Promise<FoldedColumns | null>;
}

/** Whether a perFact context drives a fold for this manifest — the render gate shares it, so "hold a
 *  tile until its fold lands" can never disagree with whether a fold is coming at all. */
export const foldActive = (manifest: Manifest | null, context: Record<string, string>): boolean =>
  !!manifest && isGroupRegime(manifest.view) && !!manifest.companionTiles && Object.keys(context).length > 0;

// Companions cached per (version, tile); fold results per (version, tile, active measures, context). Bounded.
const FOLD_CACHE_CAP = 256;
type CompanionEntry = Companion | 'missing';

/** Planes already decoded for a cached slab tile: its structure block (`@idx`, sparse) plus each partial
 *  plane fetched so far. A plane-split fetch adds only the ones not yet resident (SLAB-FORMAT §5). */
function residentPlanes(comp: CompanionEntry | undefined): Set<string> {
  const set = new Set<string>();
  if (!comp || comp === 'missing' || comp.kind !== 'slab') return set;
  if (comp.data.offsets || comp.data.cellIds) set.add('@idx');
  for (const k of Object.keys(comp.data.planes)) set.add(k);
  return set;
}

/** Per-tile folded measure columns under the active perFact context, or null when there is no context
 *  (render the baked default-context values — zero extra work). A slab tile fetches and folds only the
 *  planes the active colour measure needs; switching measure fetches just the delta plane, cached under
 *  the tile key. Companions and folds are bounded caches; a pan or zoom re-uses every fold it has done. */
export function useMeasureFold(
  manifest: Manifest | null,
  rendered: RenderedTile[],
  context: Record<string, string>,
  activeMeasures: string[],
): MeasureFold {
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

  // A slab view folds only the active measure(s) for the map (its planes are the only ones fetched); a
  // row-form bake, or a colour channel that is not a measure, folds every measure as before (no split).
  const slab = !!manifest && isSlab(manifest);
  const scoped = useMemo(() => measures.filter((m) => activeMeasures.includes(m.name)), [measures, activeMeasures]);
  const foldMeasures = slab && scoped.length ? scoped : measures;
  const foldNames = foldMeasures.map((m) => m.name);
  const activeSig = foldNames.join(',');

  // Inspect shows every inspect channel that is a measure; the others read the baked column.
  const inspectNames = useMemo(() => {
    if (!manifest) return [] as string[];
    const measureSet = new Set(measures.map((m) => m.name));
    const out = new Set<string>();
    for (const n of manifest.view.inspect?.channels ?? []) if (measureSet.has(n)) out.add(n);
    const t = manifest.view.inspect?.title;
    if (t && measureSet.has(t)) out.add(t);
    return [...out];
  }, [manifest, measures]);

  const active = foldActive(manifest, context);
  const contextSig = JSON.stringify(context);
  const renderSig = rendered.map((t) => t.key).join(',');

  // Latest context/manifest for the imperative inspect fold, which fires outside the effect (on a click).
  const ctxRef = useRef(context);
  ctxRef.current = context;
  const manifestRef = useRef(manifest);
  manifestRef.current = manifest;

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
    const needed = slab ? slabPlanesForMeasures(manifest, foldNames) : [];
    const specFor = (key: string, want: string[]): CompanionFetch | null => {
      if (slab) {
        const pack = manifest.companionPack;
        const dir = pack?.planeEntries?.[key];
        if (!pack || !dir || !manifest.companionSlab) return null;
        return { kind: 'slab', baseUrl: packBlockUrl(manifest.view.id, version, pack.file, key), codec: pack.codec, slab: manifest.companionSlab, dir, want };
      }
      const pack = packBlock(manifest.companionPack, manifest.view.id, version, key);
      return { kind: 'row', viewId: manifest.view.id, version, key, grain, pack };
    };
    (async () => {
      // Fetch + decode the missing planes in parallel on the worker pool. A slab tile fetches only the
      // active measure's planes not already resident; a row-form tile fetches its one block once. Only a
      // definitive miss (404, or a companion the fold can't use) is cached 'missing' (baked fallback, never
      // re-requested); a transient failure stays un-entered and schedules a retry.
      let transient = false;
      await Promise.all(
        rendered.map(async (t) => {
          const ck = ckey(t.key);
          const cur = companions.current.get(ck);
          if (cur === 'missing') return;
          const want = slab ? needed.filter((p) => !residentPlanes(cur).has(p)) : [];
          if (cur && (!slab || want.length === 0)) return; // row-form cached, or all needed planes resident
          const spec = specFor(t.key, want);
          if (!spec) {
            companions.current.set(ck, 'missing');
            return;
          }
          try {
            const c = await tileLoader.loadCompanion(spec);
            const prev = companions.current.get(ck);
            if (slab && c.kind === 'slab' && prev && prev !== 'missing' && prev.kind === 'slab') {
              Object.assign(prev.data.planes, c.data.planes); // merge planes under the tile key
              prev.data.decodedBytes += c.data.decodedBytes;
            } else {
              companions.current.set(ck, c);
            }
          } catch (e) {
            const msg = e instanceof Error ? e.message : String(e);
            if (/ 404 /.test(msg) || msg.includes('no mki column')) companions.current.set(ck, 'missing');
            else transient = true;
          }
        }),
      );
      if (!alive) return;
      if (transient) setTimeout(() => alive && setRetry((r) => r + 1), 1500);

      const byTile = new Map<string, FoldedColumns>();
      const missing = new Set<string>();
      for (const t of rendered) {
        const fkey = `${version}|${t.key}|${activeSig}|${contextSig}`;
        let cols = folds.current.get(fkey);
        if (!cols) {
          const comp = companions.current.get(ckey(t.key));
          if (!comp || comp === 'missing') {
            if (comp === 'missing') missing.add(t.key);
            continue;
          }
          cols =
            comp.kind === 'slab'
              ? foldSlab(comp.data, foldMeasures, ctx, t.data.count, domains)
              : foldTile(comp.data, foldMeasures, ctx, t.data.count, domains);
          folds.current.set(fkey, cols);
        }
        byTile.set(t.key, cols);
      }
      setFolded({ byTile, missing, contextSig });

      // Evict caches for tiles no longer on screen (bounded like the tile cache), then cap the fold
      // cache so a long scrub can't hold every context it visited (oldest-first, insertion order).
      const live = new Set(rendered.map((t) => ckey(t.key)));
      for (const k of companions.current.keys()) if (!live.has(k)) companions.current.delete(k);
      for (const k of folds.current.keys()) {
        const p = k.split('|');
        if (!live.has(`${p[0]}|${p[1]}`)) folds.current.delete(k);
      }
      let over = folds.current.size - FOLD_CACHE_CAP;
      if (over > 0) for (const k of folds.current.keys()) { folds.current.delete(k); if (--over <= 0) break; }
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manifest, active, contextSig, renderSig, activeSig, slab, measures, domains, retry]);

  // Inspect a clicked mark under the active context: ensure every inspect measure's planes are resident for
  // the one tile (fetch the missing ones, merge under its key), then fold them. The map fold only carries
  // the colour measure, so this is where the tooltip's other measures get their context-correct values.
  const foldInspect = useCallback(
    async (key: string): Promise<FoldedColumns | null> => {
      const m = manifestRef.current;
      if (!m || !isSlab(m)) return null;
      const ctxObj = ctxRef.current;
      if (!foldActive(m, ctxObj)) return null;
      const inspectMeasures = measures.filter((mm) => inspectNames.includes(mm.name));
      if (!inspectMeasures.length) return null;
      const ck = `${m.version}|${key}`;
      if (companions.current.get(ck) === 'missing') return null;
      const need = slabPlanesForMeasures(m, inspectNames);
      const want = need.filter((p) => !residentPlanes(companions.current.get(ck)).has(p));
      if (want.length) {
        const pack = m.companionPack;
        const dir = pack?.planeEntries?.[key];
        if (!pack || !dir || !m.companionSlab) return null;
        const spec: CompanionFetch = { kind: 'slab', baseUrl: packBlockUrl(m.view.id, m.version, pack.file, key), codec: pack.codec, slab: m.companionSlab, dir, want };
        try {
          const c = await tileLoader.loadCompanion(spec);
          const prev = companions.current.get(ck);
          if (c.kind === 'slab' && prev && prev !== 'missing' && prev.kind === 'slab') Object.assign(prev.data.planes, c.data.planes);
          else companions.current.set(ck, c);
        } catch {
          return null;
        }
      }
      const comp = companions.current.get(ck);
      if (!comp || comp === 'missing' || comp.kind !== 'slab') return null;
      return foldSlab(comp.data, inspectMeasures, buildFoldContext(m.view, ctxObj), comp.data.markCount, domains);
    },
    [measures, inspectNames, domains],
  );

  return { folded: active ? folded : null, foldInspect };
}
