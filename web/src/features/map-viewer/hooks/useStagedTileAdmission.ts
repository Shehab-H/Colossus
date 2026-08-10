import { useEffect, useMemo, useRef, useState } from 'react';
import type { RenderedTile } from './useTiles';

export interface AdmittedTile extends RenderedTile {
  /** Warm-up layers upload their attributes but do not draw until the complete cover is ready. */
  visible: boolean;
}

interface AdmissionState {
  visible: RenderedTile[];
  warming: RenderedTile[];
}

const sameTile = (a: RenderedTile, b: RenderedTile): boolean => a.key === b.key && a.data === b.data;
const sameCover = (a: readonly RenderedTile[], b: readonly RenderedTile[]): boolean =>
  a.length === b.length && a.every((tile, i) => sameTile(tile, b[i]));

/**
 * Admit at most one new deck layer per animation frame.
 *
 * A loaded cover can replace one parent with several dense children. Passing that entire array to deck
 * makes every geometry/value/filter buffer allocate and upload in the same frame. Keep the previous cover
 * visible while the replacement layers are mounted hidden, one per frame; once all of them have survived
 * a render opportunity, flip the already-resident layers visible atomically. The first cover has no
 * stand-in, so it paints progressively instead of staying blank during warm-up.
 */
export function useStagedTileAdmission(target: readonly RenderedTile[], paused = false): AdmittedTile[] {
  const visibleRef = useRef<RenderedTile[]>([]);
  const targetRef = useRef(target);
  targetRef.current = target;
  const [state, setState] = useState<AdmissionState>({ visible: [], warming: [] });

  useEffect(() => {
    // Tile fetch/decode may finish while a drag or wheel gesture is active. Do not let that completion
    // inject a GPU upload into the gesture; the latest target is picked up as soon as interaction ends.
    if (paused) return;
    const current = visibleRef.current;
    if (sameCover(current, target)) {
      if (state.warming.length) setState({ visible: current, warming: [] });
      return;
    }

    let cancelled = false;
    let raf = 0;
    let visible = current;
    let warming: RenderedTile[] = [];
    const firstPaint = visible.length === 0;
    const byCurrentKey = new Map(visible.map((tile) => [tile.key, tile]));

    // A same-id data replacement cannot coexist with its old layer as a hidden warm-up. Stage those
    // replacements one per frame too; spatial cover is unchanged, so a per-tile flip is safe.
    const replacements = target.filter((tile) => {
      const old = byCurrentKey.get(tile.key);
      return old !== undefined && old.data !== tile.data;
    });
    const additions = target.filter((tile) => !byCurrentKey.has(tile.key));
    const queue = [...replacements, ...additions];
    let index = 0;

    const step = () => {
      if (cancelled || targetRef.current !== target) return;
      if (index < queue.length) {
        const tile = queue[index++];
        const oldIndex = visible.findIndex((v) => v.key === tile.key);
        if (oldIndex >= 0) {
          visible = visible.map((v, i) => (i === oldIndex ? tile : v));
          visibleRef.current = visible;
        } else if (firstPaint) {
          visible = [...visible, tile];
          visibleRef.current = visible;
        } else {
          warming = [...warming, tile];
        }
        setState({ visible, warming });
        raf = requestAnimationFrame(step);
        return;
      }

      // The last setState must reach deck before the cover flip. One extra animation frame guarantees
      // each hidden layer has had an update opportunity, so making it visible does not upload again.
      raf = requestAnimationFrame(() => {
        if (cancelled || targetRef.current !== target) return;
        const committed = [...target];
        visibleRef.current = committed;
        setState({ visible: committed, warming: [] });
      });
    };

    raf = requestAnimationFrame(step);
    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
    };
    // `state` is deliberately not a dependency: it is the output of this admission run. A target cover
    // change cancels the run and starts from visibleRef, the last cover actually exposed to the user.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [target, paused]);

  return useMemo(() => {
    const visibleKeys = new Set(state.visible.map((tile) => tile.key));
    return [
      ...state.visible.map((tile) => ({ ...tile, visible: true })),
      ...state.warming.filter((tile) => !visibleKeys.has(tile.key)).map((tile) => ({ ...tile, visible: false })),
    ];
  }, [state]);
}
