import { useEffect, useMemo, useRef, useState } from 'react';
import type { Manifest } from '../lib/manifest';
import { isGroupRegime } from '../lib/channels';
import { buildFoldContext, type CompanionData, foldTile, type MeasureExpr, parseMeasure } from '../lib/measures';
import { columnValue, loadCompanion, type TileData } from '../lib/tileData';
import type { RenderedTile } from './useTiles';

export type FoldedColumns = Record<string, Float32Array | Uint16Array>;

/** Build a tile's mark id → index map, so the fold aligns each companion row's `mk` to a rendered mark. */
function markIndex(d: TileData): Map<string, number> {
  const idCol = d.values.id;
  const m = new Map<string, number>();
  for (let i = 0; i < d.count; i++) {
    const v = columnValue(idCol, i);
    if (typeof v === 'string') m.set(v, i);
  }
  return m;
}

/** Per-tile folded measure columns under the active perFact context, or null when there is no context
 *  (render the baked default-context values — zero extra work). The fold recomputes each measure over the
 *  tile's fact companion; companions are decoded once and cached (evicted with the tile via the version
 *  key). When null, callers colour and inspect straight from the baked tile columns, exactly as the row
 *  regime does. (GROUP-MEASURES §8.) */
export function useMeasureFold(
  manifest: Manifest | null,
  rendered: RenderedTile[],
  context: Record<string, string>,
): Map<string, FoldedColumns> | null {
  const [folded, setFolded] = useState<Map<string, FoldedColumns> | null>(null);
  const companions = useRef(new Map<string, CompanionData>());

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
    (async () => {
      const out = new Map<string, FoldedColumns>();
      for (const t of rendered) {
        const ckey = `${version}|${t.key}`;
        let comp = companions.current.get(ckey);
        if (!comp) {
          try {
            comp = await loadCompanion(manifest, version, t.key);
          } catch {
            continue; // no companion (older bake / missing tile) → this tile keeps its baked colours
          }
          if (!alive) return;
          companions.current.set(ckey, comp);
        }
        out.set(t.key, foldTile(comp, measures, ctx, t.data.count, markIndex(t.data), domains));
      }
      if (alive) setFolded(out);
      // Evict companions for tiles no longer on screen (bounded like the tile cache).
      const live = new Set(rendered.map((t) => `${version}|${t.key}`));
      for (const k of companions.current.keys()) if (!live.has(k)) companions.current.delete(k);
    })();
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [manifest, active, contextSig, renderSig, measures, domains]);

  return active ? folded : null;
}
