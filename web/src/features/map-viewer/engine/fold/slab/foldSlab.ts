import type { SlabData } from './slabDecode';
import type { MeasureExpr } from '../../view/measureGrammar';
import type { FoldContext } from '../foldContext';
import { foldSparse } from './sparseFold';
import { foldDense } from './denseFold';

/** Fold a slab tile under the active context → per-mark measure columns (indexed by mki), byte-identical to
 *  foldTile. Sparse scans entries against the cell mask; dense reads cumulative cell rows. */
export function foldSlab(
  s: SlabData,
  measures: { name: string; ast: MeasureExpr }[],
  ctx: FoldContext,
  markCount: number,
  domains: Record<string, string[]>,
): Record<string, Float32Array | Uint16Array> {
  return s.layout === 'sparse'
    ? foldSparse(s, measures, ctx, markCount, domains)
    : foldDense(s, measures, ctx, markCount, domains);
}
