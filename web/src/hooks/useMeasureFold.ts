import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { Manifest } from '../lib/manifest';
import { packBlockUrl, tileLayoutOf } from '../lib/manifest';
import { isGroupRegime } from '../lib/channels';
import { buildFoldContext, foldTile, type MeasureExpr, parseMeasure } from '../lib/measures';
import { companionGrain, type Companion, type CompanionFetch, packBlock } from '../lib/tileData';
import { denseNeeds, foldSlab, isSlab, mergeSlab, slabPlanesForMeasures } from '../lib/slab';
import { type FoldedColumns, foldRemote, isRemoteFold } from '../lib/remoteFold';
import { tileLoader } from '../lib/tileLoader';
import { timedSync } from '../lib/perf';
import type { RenderedTile } from './useTiles';

export type { FoldedColumns };

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

/** Drop fold results for tiles that have left the screen, then cap the cache (oldest-first, insertion
 *  order) so a long scrub can't hold every context it visited. `live` holds `version|tileKey` keys; a fold
 *  key is `version|tileKey|measures|context`, so its first two segments are the tile's identity. Shared by
 *  both routes — the fold-result cache keys are identical whether the fold ran here or on the server. */
function evict(folds: Map<string, FoldedColumns>, live: Set<string>): void {
  for (const k of folds.keys()) {
    const p = k.split('|');
    if (!live.has(`${p[0]}|${p[1]}`)) folds.delete(k);
  }
  let over = folds.size - FOLD_CACHE_CAP;
  if (over > 0)
    for (const k of folds.keys()) {
      folds.delete(k);
      if (--over <= 0) break;
    }
}

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
  // R4: an over-budget view (priced `remote` at bake, or forced with ?fold=remote) folds on the server —
  // same seam, same outputs, same cache keys; it fetches folded columns instead of companion planes.
  const remote = !!manifest && isRemoteFold(manifest);
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
    const fkey = (key: string) => `${version}|${key}|${activeSig}|${contextSig}`;

    // Remote route: one batched fold request for the on-screen tiles this context hasn't folded yet. No
    // companion fetch, no local fold — the seam's outputs and cache keys are identical, so everything
    // downstream (render gate, GPU buffers, eviction) is unchanged.
    if (remote) {
      (async () => {
        const need = rendered.filter((t) => !folds.current.has(fkey(t.key)));
        if (need.length) {
          try {
            const r = await foldRemote(manifest.view.id, version, foldNames, ctx, need.map((t) => t.key));
            if (!alive) return;
            for (const [key, cols] of r.byTile) folds.current.set(fkey(key), cols);
          } catch {
            if (alive) setTimeout(() => alive && setRetry((x) => x + 1), 1500);
          }
        }
        if (!alive) return;
        const byTile = new Map<string, FoldedColumns>();
        for (const t of rendered) {
          const cols = folds.current.get(fkey(t.key));
          if (cols) byTile.set(t.key, cols);
        }
        setFolded({ byTile, missing: new Set(), contextSig });
        evict(folds.current, new Set(rendered.map((t) => ckey(t.key))));
      })();
      return () => {
        alive = false;
      };
    }

    // Plan one tile's companion fetch under the active context. A dense tile slices: it fetches only the cell
    // rows the fold reads (denseNeeds, SLAB-FORMAT §5), minus those already resident, and merges them in. A
    // sparse tile fetches whole planes (plane split, R1). Returns a spec to fetch, 'cached' when nothing is
    // missing, or null on a definitive miss (no pack/dir — an older bake).
    const planTileFetch = (key: string, markCount: number, cur: CompanionEntry | undefined): CompanionFetch | 'cached' | null => {
      if (!slab) {
        if (cur) return 'cached';
        const pack = packBlock(manifest.companionPack, manifest.view.id, version, key);
        return { kind: 'row', viewId: manifest.view.id, version, key, grain, pack };
      }
      const pack = manifest.companionPack;
      const dir = pack?.planeEntries?.[key];
      if (!pack || !dir || !manifest.companionSlab) return null;
      const layout = tileLayoutOf(manifest.companionSlab, key);
      const baseUrl = packBlockUrl(manifest.view.id, version, pack.file, key);
      const active = slabPlanesForMeasures(manifest, foldNames, layout);
      const sliceDir = layout === 'dense' ? pack.sliceEntries?.[key] : undefined;
      if (layout === 'dense' && sliceDir) {
        const { cells } = denseNeeds(manifest.companionSlab, ctx, active);
        const resident = cur && cur !== 'missing' && cur.kind === 'slab' ? cur.data.residentCells : undefined;
        const delta: Record<string, number[]> = {};
        let any = false;
        for (const p of active) {
          const have = resident?.[p];
          const missing = have ? (cells[p] ?? []).filter((c) => !have.has(c)) : (cells[p] ?? []);
          delta[p] = missing;
          if (missing.length) any = true;
        }
        if (cur && cur !== 'missing' && !any) return 'cached';
        return { kind: 'slab', baseUrl, codec: pack.codec, slab: manifest.companionSlab, layout, markCount, dir, want: [], slice: { sliceDir, cells: delta } };
      }
      // Sparse (or a dense tile without a slice directory — an older bake): whole-plane split.
      const have = residentPlanes(cur);
      const want = active.filter((p) => !have.has(p));
      if (cur && cur !== 'missing' && want.length === 0) return 'cached';
      return { kind: 'slab', baseUrl, codec: pack.codec, slab: manifest.companionSlab, layout, markCount, dir, want };
    };
    (async () => {
      // Fetch + decode the missing planes/cell-rows in parallel on the worker pool. A dense slab tile fetches
      // only the context's cell rows not already resident; a sparse tile its active planes; a row-form tile
      // its one block. Only a definitive miss (404, or a companion the fold can't use) is cached 'missing'
      // (baked fallback, never re-requested); a transient failure stays un-entered and schedules a retry.
      let transient = false;
      await Promise.all(
        rendered.map(async (t) => {
          const ck = ckey(t.key);
          const cur = companions.current.get(ck);
          if (cur === 'missing') return;
          const plan = planTileFetch(t.key, t.data.count, cur);
          if (plan === 'cached') return;
          if (!plan) {
            companions.current.set(ck, 'missing');
            return;
          }
          try {
            const c = await tileLoader.loadCompanion(plan);
            const prev = companions.current.get(ck);
            if (slab && c.kind === 'slab' && prev && prev !== 'missing' && prev.kind === 'slab') {
              mergeSlab(prev.data, c.data); // incremental slice / plane merge under the tile key
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
        let cols = folds.current.get(fkey(t.key));
        if (!cols) {
          const comp = companions.current.get(ckey(t.key));
          if (!comp || comp === 'missing') {
            if (comp === 'missing') missing.add(t.key);
            continue;
          }
          // The local counterpart of fold.remote: same seam, same outputs. Measured per tile so the
          // dashboard can put a client fold's p50 next to a remote round trip and compare like for like.
          cols = timedSync(
            'fold.client',
            () =>
              comp.kind === 'slab'
                ? foldSlab(comp.data, foldMeasures, ctx, t.data.count, domains)
                : foldTile(comp.data, foldMeasures, ctx, t.data.count, domains),
            () => ({ n: t.data.count, key: t.key }),
          );
          folds.current.set(fkey(t.key), cols);
        }
        byTile.set(t.key, cols);
      }
      setFolded({ byTile, missing, contextSig });

      // Evict caches for tiles no longer on screen (bounded like the tile cache).
      const live = new Set(rendered.map((t) => ckey(t.key)));
      for (const k of companions.current.keys()) if (!live.has(k)) companions.current.delete(k);
      evict(folds.current, live);
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manifest, active, contextSig, renderSig, activeSig, slab, remote, measures, domains, retry]);

  // Inspect a clicked mark under the active context: ensure every inspect measure's planes are resident for
  // the one tile (fetch the missing ones, merge under its key), then fold them. The map fold only carries
  // the colour measure, so this is where the tooltip's other measures get their context-correct values.
  const foldInspect = useCallback(
    async (key: string): Promise<FoldedColumns | null> => {
      const m = manifestRef.current;
      if (!m) return null;
      const ctxObj = ctxRef.current;
      if (!foldActive(m, ctxObj)) return null;
      const inspectMeasures = measures.filter((mm) => inspectNames.includes(mm.name));
      if (!inspectMeasures.length) return null;
      // Remote route: the tooltip's measures fold on the server for this one tile — the same request the
      // map makes, just a different measure set (no planes to fetch or merge).
      if (isRemoteFold(m)) {
        try {
          const r = await foldRemote(m.view.id, m.version, inspectMeasures.map((mm) => mm.name),
            buildFoldContext(m.view, ctxObj), [key]);
          return r.byTile.get(key) ?? null;
        } catch {
          return null;
        }
      }
      if (!isSlab(m) || !m.companionSlab) return null;
      const ck = `${m.version}|${key}`;
      const cur = companions.current.get(ck);
      if (cur === 'missing') return null;
      const pack = m.companionPack;
      const dir = pack?.planeEntries?.[key];
      if (!pack || !dir) return null;
      const layout = tileLayoutOf(m.companionSlab, key);
      const baseUrl = packBlockUrl(m.view.id, m.version, pack.file, key);
      const ctxF = buildFoldContext(m.view, ctxObj);
      const active = slabPlanesForMeasures(m, inspectNames, layout);
      const markCount = cur && cur.kind === 'slab' ? cur.data.markCount : 0;

      // A dense tile slices the inspect measures' cell rows for this one clicked tile; a sparse tile fetches
      // their whole planes. Either way, only the not-yet-resident part is fetched and merged under the key.
      let spec: CompanionFetch | null = null;
      const sliceDir = layout === 'dense' ? pack.sliceEntries?.[key] : undefined;
      if (layout === 'dense' && sliceDir && markCount) {
        const { cells } = denseNeeds(m.companionSlab, ctxF, active);
        const resident = cur && cur.kind === 'slab' ? cur.data.residentCells : undefined;
        const delta: Record<string, number[]> = {};
        let any = false;
        for (const p of active) {
          const have = resident?.[p];
          const missing = have ? (cells[p] ?? []).filter((c) => !have.has(c)) : (cells[p] ?? []);
          delta[p] = missing;
          if (missing.length) any = true;
        }
        if (any) spec = { kind: 'slab', baseUrl, codec: pack.codec, slab: m.companionSlab, layout, markCount, dir, want: [], slice: { sliceDir, cells: delta } };
      } else {
        const want = active.filter((p) => !residentPlanes(cur).has(p));
        if (want.length) spec = { kind: 'slab', baseUrl, codec: pack.codec, slab: m.companionSlab, layout, markCount, dir, want };
      }
      if (spec) {
        try {
          const c = await tileLoader.loadCompanion(spec);
          const prev = companions.current.get(ck);
          if (c.kind === 'slab' && prev && prev !== 'missing' && prev.kind === 'slab') mergeSlab(prev.data, c.data);
          else companions.current.set(ck, c);
        } catch {
          return null;
        }
      }
      const comp = companions.current.get(ck);
      if (!comp || comp === 'missing' || comp.kind !== 'slab') return null;
      return foldSlab(comp.data, inspectMeasures, ctxF, comp.data.markCount, domains);
    },
    [measures, inspectNames, domains],
  );

  return { folded: active ? folded : null, foldInspect };
}
